using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Net.WebSockets;
using System.Linq;
using NAudio.Wave;
using Vosk;
using Supabase;
using Supabase.Postgrest;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;
using Supabase.Postgrest.Responses;
using static Supabase.Postgrest.Constants;

namespace VoskClient
{
    [Table("Sample_Plates_All_Fake_Data")]
    public class VehicleInfo : BaseModel
    {
        [PrimaryKey("id")]
        public int Id { get; set; }

        [Column("plate")]
        public string Plate { get; set; }

        [Column("year")]
        public int Year { get; set; }

        [Column("make")]
        public string Make { get; set; }

        [Column("model")]
        public string Model { get; set; }

        [Column("status")]
        public string Status { get; set; }

        public override string ToString()
        {
            return $"Vehicle: {Year} {Make} {Model}, Plate: {Plate}, Status: {Status}";
        }
    }

    // Service to handle API calls
    public class VehicleApiService
    {
        public readonly Supabase.Client supabaseClient;

        public VehicleApiService()
        {
            // Check for environment variables
            var apiUrl = Environment.GetEnvironmentVariable("SUPABASE_API_URL");
            var apiKey = Environment.GetEnvironmentVariable("SUPABASE_API_KEY");

            if (string.IsNullOrEmpty(apiUrl) || string.IsNullOrEmpty(apiKey))
            {
                Console.WriteLine("[-] Warning: SUPABASE_API_URL or SUPABASE_API_KEY environment variables not set");
            }
            else
            {
                Console.WriteLine("[+] Supabase API configuration found");
                var options = new SupabaseOptions
                {
                    AutoRefreshToken = true,
                    AutoConnectRealtime = false
                };
                supabaseClient = new Supabase.Client(apiUrl, apiKey, options);
            }
        }

        public async Task<VehicleInfo> GetVehicleInfoAsync(string plateNumber)
        {
            if (supabaseClient == null)
            {
                throw new InvalidOperationException("Supabase client not initialized. Check API URL and API Key configuration");
            }

            try
            {
                // Query using the Supabase SDK
                Console.WriteLine($"[*] Querying plate: {plateNumber}");

                var response = await supabaseClient
                    .From<VehicleInfo>()
                    .Filter(v => v.Plate, Operator.WFTS, new FullTextSearchConfig(plateNumber, "english"))
                    .Get();
                
                if (response.Models.Count > 0)
                {
                    return response.Models.FirstOrDefault();
                }
                else
                {
                    Console.WriteLine($"[-] No vehicle found with plate {plateNumber}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[-] Error retrieving vehicle information: {ex.Message}");
                return null;
            }
        }
    }

    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("[+] Vosk License Plate STT...");

            // Check environment variables
            var supabaseUrl = Environment.GetEnvironmentVariable("SUPABASE_API_URL");
            var supabaseKey = Environment.GetEnvironmentVariable("SUPABASE_API_KEY");
            
            Console.WriteLine($"[*] Supabase API URL configured: {!string.IsNullOrEmpty(supabaseUrl)}");
            Console.WriteLine($"[*] Supabase API Key configured: {!string.IsNullOrEmpty(supabaseKey)}");

            // Initialize API service
            var apiService = new VehicleApiService();

            // Print out all of the plate number we have
            var everything = await apiService.supabaseClient.From<VehicleInfo>().Get();
            foreach (var vehicle in everything.Models)
            {
                Console.WriteLine($"[*] Vehicle: {vehicle.Year} {vehicle.Make} {vehicle.Model}, Plate: {vehicle.Plate}, Status: {vehicle.Status}");
            }

            const string modelPath = "vosk-model-small-en-us-0.15/";

            if (!Directory.Exists(modelPath))
            {
                Console.WriteLine("[-] Vosk model not found!");
                return;
            }

            Vosk.Vosk.SetLogLevel(0);
            var model = new Model(modelPath);

            var grammar = new string[]
            {
                "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m",
                "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z",
                "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
                "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel",
                "india", "juliett", "kilo", "lima", "mike", "november", "oscar", "papa",
                "quebec", "romeo", "sierra", "tango", "uniform", "victor", "whiskey",
                "x-ray", "yankee", "zulu",
            };

            var grammarJSON = JsonSerializer.Serialize(grammar);
            var recognizer = new VoskRecognizer(model, 16000.0f, grammarJSON);
            recognizer.SetMaxAlternatives(3);

            var audioService = new AudioService(apiService);
            Console.WriteLine("Press ENTER to start real-time capture...");
            Console.ReadLine();
            await audioService.StreamAudioAsync(recognizer);

            recognizer.Dispose();
            model.Dispose();
        }
    }

    public class AudioService
    {
        private readonly VehicleApiService _apiService;
        private readonly StringBuilder _plateBuilder = new StringBuilder();
        private bool _isCollectingPlate = false;
        private DateTime _lastCharTime = DateTime.MinValue;
        private readonly TimeSpan _resetTimeout = TimeSpan.FromSeconds(3);

        public AudioService(VehicleApiService apiService)
        {
            _apiService = apiService;
        }

        public async Task StreamAudioAsync(VoskRecognizer recognizer)
        {
            var waveFormat = new WaveFormat(16000, 1);
            using var waveIn = new WaveInEvent
            {
                WaveFormat = waveFormat,
                BufferMilliseconds = 500
            };

            waveIn.DataAvailable += async (s, a) =>
            {
                // STT
                if (recognizer.AcceptWaveform(a.Buffer, a.BytesRecorded))
                {
                    var result = recognizer.Result();
                    Console.WriteLine("[+] Final: " + result);
                    
                    // Parse the Vosk response and extract the highest confidence text
                    string bestText = ExtractHighestConfidenceText(result);
                    if (!string.IsNullOrEmpty(bestText))
                    {
                        await ProcessRecognizedText(bestText);
                    }
                    
                }
                else
                {
                    var interim = recognizer.PartialResult();
                }
            };

            waveIn.StartRecording();
            Console.WriteLine("[*] Streaming... Press ENTER to stop.");
            await Task.Run(() => Console.ReadLine());
            waveIn.StopRecording();
        }

        private string ExtractHighestConfidenceText(string voskJson)
        {
            try
            {
                using var document = JsonDocument.Parse(voskJson);
                var root = document.RootElement;
                
                if (root.TryGetProperty("alternatives", out var alternatives))
                {
                    // Direct structure from Vosk
                    return GetBestAlternative(alternatives);
                }
                else if (root.TryGetProperty("payload", out var payload) && 
                         payload.TryGetProperty("alternatives", out alternatives))
                {
                    // Nested structure that includes type
                    return GetBestAlternative(alternatives);
                }
                
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[-] Error parsing Vosk result: {ex.Message}");
                return null;
            }
        }

        private string GetBestAlternative(JsonElement alternatives)
        {
            if (alternatives.ValueKind != JsonValueKind.Array || alternatives.GetArrayLength() == 0)
                return null;
                
            double bestConfidence = -1;
            string bestText = null;
            
            foreach (var alt in alternatives.EnumerateArray())
            {
                if (alt.TryGetProperty("confidence", out var confElement) && 
                    alt.TryGetProperty("text", out var textElement))
                {
                    double confidence = confElement.GetDouble();
                    string text = textElement.GetString();
                    
                    if (confidence > bestConfidence)
                    {
                        bestConfidence = confidence;
                        bestText = text;
                    }
                }
            }
            
            return bestText;
        }

        private async Task ProcessRecognizedText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
                
            // Check if we need to reset the plate collection due to timeout
            if (_isCollectingPlate && (DateTime.Now - _lastCharTime) > _resetTimeout)
            {
                Console.WriteLine($"[*] Plate collection timeout. Resetting from: {_plateBuilder}");
                _plateBuilder.Clear();
                _isCollectingPlate = false;
            }
            
            // Update last character time
            _lastCharTime = DateTime.Now;
            
            // Normalize the recognized text to convert words to characters
            string normalized = NormalizeText(text);
            Console.WriteLine($"[*] Normalized input: '{text}' -> '{normalized}'");
            
            if (!string.IsNullOrEmpty(normalized))
            {
                _isCollectingPlate = true;
                _plateBuilder.Append(normalized);
                
                // Only query when we have a reasonable plate length (typically 5-8 chars)
                if (_plateBuilder.Length >= 5)
                {
                    string plateToQuery = _plateBuilder.ToString();
                    
                    // Query the API
                    var vehicleInfo = await _apiService.GetVehicleInfoAsync(plateToQuery);
                    if (vehicleInfo != null)
                    {
                        Console.WriteLine($"[*] Vehicle found: {vehicleInfo}");
                        // Reset after successful lookup
                        _plateBuilder.Clear();
                        _isCollectingPlate = false;
                    }
                    else
                    {
                        // Could be partial plate, continue collecting
                        Console.WriteLine($"[*] No match for {plateToQuery}, continuing collection");
                    }
                }
            }
        }

        private string NormalizeText(string text)
        {
            // Convert spoken digits and letters to actual characters
            Dictionary<string, string> wordToChar = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // Numbers
                {"zero", "0"}, {"one", "1"}, {"two", "2"}, {"three", "3"}, {"four", "4"},
                {"five", "5"}, {"six", "6"}, {"seven", "7"}, {"eight", "8"}, {"nine", "9"},
                
                // NATO phonetic alphabet
                {"alpha", "A"}, {"bravo", "B"}, {"charlie", "C"}, {"delta", "D"}, 
                {"echo", "E"}, {"foxtrot", "F"}, {"golf", "G"}, {"hotel", "H"},
                {"india", "I"}, {"juliett", "J"}, {"juliet", "J"}, {"kilo", "K"}, 
                {"lima", "L"}, {"mike", "M"}, {"november", "N"}, {"oscar", "O"}, 
                {"papa", "P"}, {"quebec", "Q"}, {"romeo", "R"}, {"sierra", "S"}, 
                {"tango", "T"}, {"uniform", "U"}, {"victor", "V"}, {"whiskey", "W"},
                {"xray", "X"}, {"x-ray", "X"}, {"yankee", "Y"}, {"zulu", "Z"},
                
                // Single letters
                {"a", "A"}, {"b", "B"}, {"c", "C"}, {"d", "D"}, {"e", "E"},
                {"f", "F"}, {"g", "G"}, {"h", "H"}, {"i", "I"}, {"j", "J"},
                {"k", "K"}, {"l", "L"}, {"m", "M"}, {"n", "N"}, {"o", "O"},
                {"p", "P"}, {"q", "Q"}, {"r", "R"}, {"s", "S"}, {"t", "T"},
                {"u", "U"}, {"v", "V"}, {"w", "W"}, {"x", "X"}, {"y", "Y"}, {"z", "Z"}
            };

            // Split the input text into individual words
            string[] words = text.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            StringBuilder normalizedText = new StringBuilder();
            
            foreach (string word in words)
            {
                if (wordToChar.TryGetValue(word, out string charValue))
                {
                    normalizedText.Append(charValue);
                }
                else
                {
                    // If not found in our mappings, keep the original word as-is
                    // You could enhance this with custom logic for specific cases
                    normalizedText.Append(word);
                }
            }
            
            return normalizedText.ToString();
        }
    }
}
