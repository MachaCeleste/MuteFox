using NAudio.Wave;
using Whisper.net;
using Whisper.net.Logger;

namespace MuteFox;

public abstract class STT
{
    private WaveInEvent _waveIn;
    private MemoryStream _audioStream;
    private WhisperFactory _whisperFactory;
    private WhisperProcessor _processor;
    private bool _isRecording = false;

    public void Init(string modelPath)
    {
        LogProvider.AddLogger((level, message) =>
        {
            Log($"[{level}] {message}");
        });
        _whisperFactory = WhisperFactory.FromPath(modelPath);
        _processor = _whisperFactory.CreateBuilder().WithLanguage("en").Build();
    }

    public bool GetIsRecording() => _isRecording;

    public void StartListening()
    {
        if (_isRecording) return;
        _isRecording = true;

        _audioStream = new MemoryStream();
        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 16, 1)
        };

        _waveIn.DataAvailable += (sender, args) => _audioStream.Write(args.Buffer, 0, args.BytesRecorded);

        _waveIn.StartRecording();
        Log("Listening...");
    }

    public async Task StopListening()
    {
        if (!_isRecording) return;

        Log("Stopping Listening...");

        _waveIn.StopRecording();
        _isRecording = false;

        var outStream = new MemoryStream();
        using (var writer = new WaveFileWriter(outStream, _waveIn.WaveFormat))
        {
            _audioStream.Position = 0;
            _audioStream.CopyTo(writer);
            writer.Flush();

            outStream.Position = 0;

            Log("Processing audio...");

            var outputText = string.Empty;
            await foreach (var res in _processor.ProcessAsync(outStream)) outputText += res.Text;

            _waveIn.Dispose();
            _audioStream.Dispose();

            Log("Done.");

            Output(outputText.Trim());
        }
    }

    public abstract void Output(string message);

    public abstract void Log(string message);
}
