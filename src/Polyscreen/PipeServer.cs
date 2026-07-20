using System.IO.Pipes;
using System.Text;

namespace Polyscreen;

/// <summary>
/// Named-pipe command interface so the exe doubles as a CLI:
///   Polyscreen.exe assign left notepad
///   Polyscreen.exe release notepad | release all
///   Polyscreen.exe list | layout halves | reload | quit
/// One request line per connection, response text back, then close.
/// </summary>
public class PipeServer : IDisposable
{
    public const string PipeName = "PolyscreenPipe";
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly Control _marshal;
    private readonly Func<string[], string> _handler;
    private volatile bool _stopping;

    public PipeServer(Control marshal, Func<string[], string> handler)
    {
        _marshal = marshal;
        _handler = handler;
        new Thread(ListenLoop) { IsBackground = true, Name = "PolyscreenPipe" }.Start();
    }

    private void ListenLoop()
    {
        while (!_stopping)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1);
                server.WaitForConnection();
                Log.Write("pipe: client connected");

                using var reader = new StreamReader(server, Utf8NoBom, false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(server, Utf8NoBom, 1024, leaveOpen: true) { AutoFlush = true };

                var line = reader.ReadLine();
                if (line == null) continue;
                Log.Write($"pipe: request '{line}'");

                var args = SplitArgs(line);
                // Engine state lives on the UI thread; marshal the command there.
                string response = (string)_marshal.Invoke(() => SafeHandle(args))!;

                writer.Write(response);
                writer.Flush();
                server.WaitForPipeDrain();
                Log.Write("pipe: response sent");
            }
            catch (Exception ex)
            {
                if (!_stopping) Log.Write("pipe: " + ex.Message);
            }
        }
    }

    private string SafeHandle(string[] args)
    {
        try { return _handler(args); }
        catch (Exception ex) { return "error: " + ex.Message; }
    }

    private static string[] SplitArgs(string line)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        foreach (char c in line)
        {
            if (c == '"') quoted = !quoted;
            else if (c == ' ' && !quoted)
            {
                if (current.Length > 0) { args.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(c);
        }
        if (current.Length > 0) args.Add(current.ToString());
        return args.ToArray();
    }

    /// <summary>Client side. Returns null (with an explanation in error) if no instance answers.</summary>
    public static string? SendCommand(string[] args, out string? error, int timeoutMs = 5000)
    {
        var task = Task.Run(() =>
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            client.Connect(timeoutMs);
            var writer = new StreamWriter(client, Utf8NoBom, 1024, leaveOpen: true) { AutoFlush = true };
            var reader = new StreamReader(client, Utf8NoBom, false, 1024, leaveOpen: true);
            writer.WriteLine(string.Join(' ', args.Select(a => a.Contains(' ') ? '"' + a + '"' : a)));
            return reader.ReadToEnd();
        });
        try
        {
            if (task.Wait(timeoutMs))
            {
                error = null;
                return task.Result;
            }
            error = "timed out waiting for a response";
            return null;
        }
        catch (AggregateException ex) when (ex.InnerException is TimeoutException)
        {
            error = "no running instance (pipe connect timed out)";
            return null;
        }
        catch (Exception ex)
        {
            error = (ex as AggregateException)?.InnerException?.ToString() ?? ex.ToString();
            return null;
        }
    }

    public void Dispose()
    {
        _stopping = true;
        // Unblock WaitForConnection so the thread can exit.
        try
        {
            using var poke = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            poke.Connect(200);
        }
        catch { }
    }
}
