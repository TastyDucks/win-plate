using System;
using System.IO;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.Logger;

namespace WhisperClient
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("[+] Whisper Local STT starting...");
            
            var ggmlType = GgmlType.Base;
            var modelFileName = "ggml-base.bin";

            if (!File.Exists(modelFileName))
            {
                await DownloadModel(modelFileName, ggmlType);
            }

            var audioService = new AudioService();
            Console.WriteLine("Press ENTER to record 5 seconds...");
            Console.ReadLine();
            var wavData = await audioService.CaptureAudioAsync(TimeSpan.FromSeconds(5));

            using var wavStream = new MemoryStream(wavData);
            using var reader = new WaveFileReader(wavStream);
            var resampler = new WdlResamplingSampleProvider(reader.ToSampleProvider(), 16000);
            using var processedStream = new MemoryStream();
            WaveFileWriter.WriteWavFileToStream(processedStream, resampler.ToWaveProvider16());
            processedStream.Seek(0, SeekOrigin.Begin);
            using var whisperLogger = LogProvider.AddConsoleLogging(WhisperLogLevel.Debug);
            using var whisperFactory = WhisperFactory.FromPath(modelFileName);
            using var processor = whisperFactory.CreateBuilder()
                .WithLanguage("auto")
                .Build();

            Console.WriteLine("[+] Transcribing locally...");
            await foreach (var result in processor.ProcessAsync(processedStream))
            {
                Console.WriteLine($"{result.Start}->{result.End}: {result.Text}");
            }
        }

    private static async Task DownloadModel(string fileName, GgmlType ggmlType)
    {
        Console.WriteLine($"Downloading Model {fileName}");
        using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(ggmlType);
        using var fileWriter = File.OpenWrite(fileName);
        await modelStream.CopyToAsync(fileWriter);
    }
    }

    public class AudioService
    {
        public async Task<byte[]> CaptureAudioAsync(TimeSpan duration)
        {
            var waveFormat = new WaveFormat(44100, 1); // Capture at higher fidelity (44.1kHz mono)
            using var waveIn = new WaveInEvent { WaveFormat = waveFormat };
            using var ms = new MemoryStream();
            var writer = new WaveFileWriter(ms, waveFormat);

            waveIn.DataAvailable += (s, a) =>
            {
                writer.Write(a.Buffer, 0, a.BytesRecorded);
            };

            waveIn.StartRecording();
            await Task.Delay(duration);
            waveIn.StopRecording();

            writer.Flush();
            return ms.ToArray();
        }
    }
}
