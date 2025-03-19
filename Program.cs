using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Navigation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;
using Whisper.net.Ggml;

namespace WhisperClient
{
    public partial class App : Application
    {
        public App()
        {
            this.InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            var window = new MainWindow();
            window.Activate();
        }
    }

    public sealed partial class MainWindow : Window
    {
        private readonly AudioService _audioService;
        private readonly WhisperProcessor _whisperProcessor;

        public MainWindow()
        {
            this.InitializeComponent();
            _audioService = new AudioService();
            _whisperProcessor = new WhisperProcessor();
        }

        private async void OnRecordClicked(object sender, RoutedEventArgs e)
        {
            TranscriptionBlock.Text = "Recording...";
            var wavData = await _audioService.CaptureAudioAsync(TimeSpan.FromSeconds(5));
            TranscriptionBlock.Text = "Processing...";
            var transcription = await _whisperProcessor.ProcessAudioAsync(wavData);
            TranscriptionBlock.Text = transcription;
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

    public class WhisperProcessor
    {
        private readonly WhisperFactory _whisperFactory;

        public WhisperProcessor()
        {
            var modelFileName = "ggml-base.bin";
            if (!File.Exists(modelFileName))
            {
                DownloadModel(modelFileName, GgmlType.Base).Wait();
            }
            _whisperFactory = WhisperFactory.FromPath(modelFileName);
        }

        public async Task<string> ProcessAudioAsync(byte[] wavData)
        {
            using var wavStream = new MemoryStream(wavData);
            using var reader = new WaveFileReader(wavStream);
            var resampler = new WdlResamplingSampleProvider(reader.ToSampleProvider(), 16000);
            using var processedStream = new MemoryStream();
            WaveFileWriter.WriteWavFileToStream(processedStream, resampler.ToWaveProvider16());
            processedStream.Seek(0, SeekOrigin.Begin);

            using var processor = _whisperFactory.CreateBuilder()
                .WithLanguage("auto")
                .Build();

            string output = "";
            await foreach (var result in processor.ProcessAsync(processedStream))
            {
                output += $"{result.Start}->{result.End}: {result.Text}\n";
            }
            return output;
        }

        private static async Task DownloadModel(string fileName, GgmlType ggmlType)
        {
            using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(ggmlType);
            using var fileWriter = File.OpenWrite(fileName);
            await modelStream.CopyToAsync(fileWriter);
        }
    }
}
