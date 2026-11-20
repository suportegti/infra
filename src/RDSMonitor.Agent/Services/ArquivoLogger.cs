using System.Collections.Concurrent;
using System.Text;

namespace RDSMonitor.Agent.Services;

/// <summary>
/// Log local do agente em arquivo texto: &lt;pasta do agente&gt;\logs\agente-AAAAMMDD.log.
/// Grava avisos e erros de tudo, e as mensagens informativas do próprio agente
/// (comandos recebidos, resultados, atualizações) com o detalhe completo das
/// exceções — o Visualizador de Eventos corta mensagens longas. Guarda 30 dias.
/// </summary>
public sealed class ArquivoLoggerProvider : ILoggerProvider
{
    private const int DiasGuardados = 30;

    private readonly string _pasta;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, ArquivoLogger> _loggers = new();
    private DateTime _ultimaLimpeza = DateTime.MinValue;

    public ArquivoLoggerProvider(string pasta)
    {
        _pasta = pasta;
        try { Directory.CreateDirectory(_pasta); } catch { /* sem permissão: segue só com o Event Log */ }
    }

    public string Pasta => _pasta;

    public ILogger CreateLogger(string categoria) => _loggers.GetOrAdd(categoria, c => new ArquivoLogger(this, c));

    internal void Gravar(string linha)
    {
        try
        {
            lock (_lock)
            {
                var hoje = DateTime.Now;
                File.AppendAllText(Path.Combine(_pasta, $"agente-{hoje:yyyyMMdd}.log"), linha, Encoding.UTF8);

                if (hoje.Date != _ultimaLimpeza.Date)
                {
                    _ultimaLimpeza = hoje;
                    foreach (var antigo in Directory.GetFiles(_pasta, "agente-*.log")
                                 .Where(f => File.GetLastWriteTime(f) < hoje.AddDays(-DiasGuardados)))
                        File.Delete(antigo);
                }
            }
        }
        catch
        {
            // Log nunca pode derrubar o agente
        }
    }

    public void Dispose() { }

    private sealed class ArquivoLogger : ILogger
    {
        private readonly ArquivoLoggerProvider _dono;
        private readonly string _categoria;
        private readonly bool _doAgente;

        public ArquivoLogger(ArquivoLoggerProvider dono, string categoria)
        {
            _dono      = dono;
            _categoria = categoria;
            _doAgente  = categoria.StartsWith("RDSMonitor", StringComparison.Ordinal);
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel nivel) =>
            nivel >= LogLevel.Warning || (_doAgente && nivel >= LogLevel.Information);

        public void Log<TState>(LogLevel nivel, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatar)
        {
            if (!IsEnabled(nivel)) return;
            var sb = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ')
                .Append(nivel switch
                {
                    LogLevel.Information => "INFO ",
                    LogLevel.Warning     => "AVISO",
                    LogLevel.Error       => "ERRO ",
                    LogLevel.Critical    => "CRIT ",
                    _                    => nivel.ToString().ToUpperInvariant(),
                })
                .Append(' ').Append(_categoria.Replace("RDSMonitor.Agent.", "")).Append(": ")
                .AppendLine(formatar(state, ex));
            if (ex is not null) sb.AppendLine(ex.ToString());
            _dono.Gravar(sb.ToString());
        }
    }
}
