using NAudio.Wave;
using Whisper.net;
using Whisper.net.Logger;

namespace MuteFox;

public abstract class STT
{
    public bool debugLogging = true;
    public int pauseDelay = 1500;

    private WaveInEvent _waveIn;
    private MemoryStream _audioStream;
    private WhisperFactory _whisperFactory;
    private WhisperProcessor _processor;
    private bool _isRecording = false;

    private CancellationTokenSource _streamingCts;
    private float _volumeThreshold = 0.02f;
    private DateTime _lastSpeechTime = DateTime.Now;

    public void Init(string modelPath)
    {
        LogProvider.AddLogger((level, message) =>
        {
            if (debugLogging) Log($"[{level}] {message}");
        });
        _whisperFactory = WhisperFactory.FromPath(modelPath);
        _processor = _whisperFactory.CreateBuilder()
            .WithLanguage("en")
            .WithNoSpeechThreshold(0.3f)
            .Build();
    }

    public bool GetIsRecording() => _isRecording;

    public void SetNoiseFloor(float db)
    {
        if (db > 0) db = 0;
        _volumeThreshold = (float)Math.Pow(10, db / 20);
    }

    public async Task CallibrateNoiseFloor(int duration = 2000)
    {
        Log("Callibrating noise floor, please wait quietly...");
        _waveIn = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1) };
        float maxDetected = 0;

        EventHandler<WaveInEventArgs> callibrationHandler = (sender, args) =>
        {
            for (int i = 0; i < args.BytesRecorded; i += 2)
            {
                short sample = (short)((args.Buffer[i + 1] << 8) | args.Buffer[i]);
                float linear = Math.Abs(sample / 32768f);
                if (linear > maxDetected) maxDetected = linear;
            }
        };

        _waveIn.DataAvailable += callibrationHandler;
        _waveIn.StartRecording();
        await Task.Delay(duration);
        _waveIn.StopRecording();
        _waveIn.DataAvailable -= callibrationHandler;

        _volumeThreshold = maxDetected * 1.2f;

        float db = 20 * (float)Math.Log10(_volumeThreshold);
        Log($"Calibration completed, Noise floor: {db:F1}dB.");
        _waveIn.Dispose();
    }

    public void StartListening()
    {
        if (_isRecording) return;
        _isRecording = true;

        _audioStream = new MemoryStream();
        _waveIn = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1) };

        _waveIn.DataAvailable += OnDataAvailable;

        _streamingCts = new CancellationTokenSource();
        _waveIn.StartRecording();

        _ = Task.Run(() => SlidingWindowLoop(_streamingCts.Token));

        Log("Listening...");
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        _audioStream.Write(args.Buffer, 0, args.BytesRecorded);

        float max = 0;
        for (int i = 0; i < args.BytesRecorded; i += 2)
        {
            short sample = (short)((args.Buffer[i + 1] << 8) | args.Buffer[i]);
            var sample32 = sample / 32768f;
            if (sample32 < 0) sample32 = -sample32;
            if (sample32 > max) max = sample32;
        }

        if (max > _volumeThreshold) _lastSpeechTime = DateTime.Now;
    }

    private async Task SlidingWindowLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(500, token);

            if (_audioStream.Length < 16000) continue;

            if ((DateTime.Now - _lastSpeechTime).TotalMilliseconds > pauseDelay)
            {
                await FinalizeSegment();
                continue;
            }

            var partialText = await ProcessBufferAsync();
            if (!string.IsNullOrWhiteSpace(partialText) && partialText.ToLower().Trim() != "thank you.") OutputSegment(partialText);
        }
    }

    private async Task<string> ProcessBufferAsync()
    {
        var outStream = new MemoryStream();
        using (var writer = new WaveFileWriter(outStream, _waveIn.WaveFormat))
        {
            lock (_audioStream)
            {
                var pos = _audioStream.Position;
                _audioStream.Position = 0;
                _audioStream.CopyTo(writer);
                _audioStream.Position = pos;
            }
            writer.Flush();
            outStream.Position = 0;

            string output = string.Empty;
            await foreach (var res in _processor.ProcessAsync(outStream))
                if (res.NoSpeechProbability < 0.8f && res.Text.Length > 2)
                    output += res.Text;
            return output.Trim();
        }
    }

    private async Task FinalizeSegment()
    {
        var final = await ProcessBufferAsync();
        if (!string.IsNullOrWhiteSpace(final) && final.ToLower().Trim() != "thank you.") Output(final);

        lock (_audioStream)
        {
            _audioStream.SetLength(0);
            _audioStream.Position = 0;
        }
        Log("Finished Processing");
    }

    public async Task StopListening()
    {
        if (!_isRecording) return;
        _streamingCts.Cancel();
        _isRecording = false;
        _waveIn.StopRecording();

        await FinalizeSegment();

        _waveIn.Dispose();
        _audioStream.Dispose();
    }

    public abstract void Output(string message);
    public abstract void OutputSegment(string message);
    public abstract void Log(string message);
}
