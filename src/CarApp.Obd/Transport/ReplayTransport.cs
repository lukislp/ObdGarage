using System.Text;

namespace CarApp.Obd.Transport;

/// <summary>
/// Simulierter Transport für Tests und Entwicklung ohne Auto:
/// beantwortet Befehle aus einem Skript (Befehl → Rohantwort inkl. Prompt '&gt;').
/// </summary>
public sealed class ReplayTransport : IObdTransport
{
    private readonly Dictionary<string, Queue<string>> _script = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _sentCommands = [];
    private byte[] _pending = [];
    private int _pendingOffset;

    public bool IsConnected { get; private set; }

    /// <summary>Alle Befehle, die der Client gesendet hat (für Assertions).</summary>
    public IReadOnlyList<string> SentCommands => _sentCommands;

    /// <summary>Standardantwort für Befehle ohne Skripteintrag.</summary>
    public string DefaultResponse { get; set; } = "?\r\r>";

    /// <summary>Registriert eine Antwort. Mehrfachaufrufe stapeln Antworten (FIFO).</summary>
    public ReplayTransport OnCommand(string command, string response)
    {
        var key = command.Replace(" ", "").ToUpperInvariant();
        if (!_script.TryGetValue(key, out var queue))
            _script[key] = queue = new Queue<string>();
        queue.Enqueue(response);
        return this;
    }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var command = Encoding.ASCII.GetString(data.Span).TrimEnd('\r', '\n');
        _sentCommands.Add(command);

        var key = command.Replace(" ", "").ToUpperInvariant();
        string response;
        if (_script.TryGetValue(key, out var queue) && queue.Count > 0)
            response = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
        else
            response = DefaultResponse;

        _pending = Encoding.ASCII.GetBytes(response);
        _pendingOffset = 0;
        return Task.CompletedTask;
    }

    public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_pendingOffset >= _pending.Length)
            return Task.FromResult(0);

        var n = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
        _pending.AsMemory(_pendingOffset, n).CopyTo(buffer);
        _pendingOffset += n;
        return Task.FromResult(n);
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }
}
