using Microsoft.EntityFrameworkCore;
using RDSMonitor.Web.Data;
using RDSMonitor.Web.Data.Entities;
using RDSMonitor.Web.Security;

namespace RDSMonitor.Web.Services;

public enum PerfilOperacao
{
    Sucesso,
    NaoEncontrado,
    NomeInvalido,
    NomeJaExiste,
    PerfilProtegido,
    PossuiUsuarios,
}

public sealed record PerfilResumo(Perfil Perfil, int Usuarios);

public interface IPerfilService
{
    Task<List<PerfilResumo>> ListarAsync();
    Task<List<Perfil>> ListarParaSelecaoAsync();
    Task<(PerfilOperacao Resultado, int? Id)> CriarAsync(string nome, string? descricao, int? copiarDeId, string operador);
    Task<PerfilOperacao> SalvarAsync(int id, string nome, string? descricao, IEnumerable<string> permissoes, string operador);
    Task<PerfilOperacao> RestaurarPadraoAsync(int id, string operador);
    Task<PerfilOperacao> ExcluirAsync(int id);
}

public class PerfilService : IPerfilService
{
    private readonly AppDbContext _db;
    private readonly IPermissaoService _permissoes;

    public PerfilService(AppDbContext db, IPermissaoService permissoes)
    {
        _db = db;
        _permissoes = permissoes;
    }

    public async Task<List<PerfilResumo>> ListarAsync()
    {
        var perfis = await _db.Perfis.AsNoTracking()
            .OrderByDescending(p => p.Sistema).ThenBy(p => p.Nome)
            .Select(p => new { Perfil = p, Usuarios = p.Usuarios.Count })
            .ToListAsync();
        return perfis.Select(x => new PerfilResumo(x.Perfil, x.Usuarios)).ToList();
    }

    public Task<List<Perfil>> ListarParaSelecaoAsync() =>
        _db.Perfis.AsNoTracking().OrderByDescending(p => p.Sistema).ThenBy(p => p.Nome).ToListAsync();

    public async Task<(PerfilOperacao, int?)> CriarAsync(string nome, string? descricao, int? copiarDeId, string operador)
    {
        nome = (nome ?? string.Empty).Trim();
        if (nome.Length is < 2 or > 60) return (PerfilOperacao.NomeInvalido, null);
        if (await NomeEmUsoAsync(nome, null)) return (PerfilOperacao.NomeJaExiste, null);

        var perfil = new Perfil
        {
            Nome        = nome,
            Descricao   = Limpar(descricao),
            AlteradoEm  = DateTime.UtcNow,
            AlteradoPor = operador,
        };

        if (copiarDeId is { } origemId && await _db.Perfis.AsNoTracking().FirstOrDefaultAsync(p => p.Id == origemId) is { } origem)
            perfil.DefinirPermissoes(origem.ListaPermissoes);

        _db.Perfis.Add(perfil);
        await _db.SaveChangesAsync();
        return (PerfilOperacao.Sucesso, perfil.Id);
    }

    public async Task<PerfilOperacao> SalvarAsync(int id, string nome, string? descricao, IEnumerable<string> permissoes, string operador)
    {
        var perfil = await _db.Perfis.FindAsync(id);
        if (perfil is null) return PerfilOperacao.NaoEncontrado;
        if (perfil.AcessoTotal) return PerfilOperacao.PerfilProtegido;

        nome = (nome ?? string.Empty).Trim();
        if (!perfil.Sistema)
        {
            if (nome.Length is < 2 or > 60) return PerfilOperacao.NomeInvalido;
            if (await NomeEmUsoAsync(nome, id)) return PerfilOperacao.NomeJaExiste;
            perfil.Nome = nome;   // perfis do sistema mantêm o nome
        }

        perfil.Descricao   = Limpar(descricao);
        perfil.DefinirPermissoes(permissoes);
        perfil.AlteradoEm  = DateTime.UtcNow;
        perfil.AlteradoPor = operador;

        await _db.SaveChangesAsync();
        _permissoes.Invalidar();
        return PerfilOperacao.Sucesso;
    }

    public async Task<PerfilOperacao> RestaurarPadraoAsync(int id, string operador)
    {
        var perfil = await _db.Perfis.FindAsync(id);
        if (perfil is null) return PerfilOperacao.NaoEncontrado;
        if (!perfil.Sistema || perfil.AcessoTotal) return PerfilOperacao.PerfilProtegido;

        perfil.DefinirPermissoes(Permissoes.PadraoDoPerfil(perfil.Nome));
        perfil.AlteradoEm  = DateTime.UtcNow;
        perfil.AlteradoPor = operador;

        await _db.SaveChangesAsync();
        _permissoes.Invalidar();
        return PerfilOperacao.Sucesso;
    }

    public async Task<PerfilOperacao> ExcluirAsync(int id)
    {
        var perfil = await _db.Perfis.FindAsync(id);
        if (perfil is null) return PerfilOperacao.NaoEncontrado;
        if (perfil.Sistema) return PerfilOperacao.PerfilProtegido;
        if (await _db.Users.AnyAsync(u => u.PerfilId == id)) return PerfilOperacao.PossuiUsuarios;

        _db.Perfis.Remove(perfil);
        await _db.SaveChangesAsync();
        _permissoes.Invalidar();
        return PerfilOperacao.Sucesso;
    }

    private Task<bool> NomeEmUsoAsync(string nome, int? ignorarId)
    {
        var minusculo = nome.ToLower();
        return _db.Perfis.AnyAsync(p => p.Nome.ToLower() == minusculo && p.Id != ignorarId);
    }

    private static string? Limpar(string? texto)
    {
        texto = texto?.Trim();
        if (string.IsNullOrEmpty(texto)) return null;
        return texto.Length > 250 ? texto[..250] : texto;
    }
}
