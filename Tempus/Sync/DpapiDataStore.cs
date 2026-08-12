using System.IO;
using System.Security.Cryptography;
using System.Text;
using Google.Apis.Json;
using Google.Apis.Util.Store;

namespace Tempus.Sync;

/// <summary>
/// Guarda o token OAuth em disco cifrado com DPAPI, vinculado ao usuário do Windows.
/// <para>
/// O <c>FileDataStore</c> que vem com a biblioteca do Google grava JSON em claro — qualquer
/// processo rodando como você leria o refresh token. Regra 5 do CLAUDE.md exige DPAPI, e é uma
/// interface de quatro métodos, então não há motivo para aceitar o padrão.
/// </para>
/// </summary>
internal sealed class DpapiDataStore : IDataStore
{
    /// <summary>
    /// Entropia adicional: um token copiado para outra máquina, ou lido por outro app do mesmo
    /// usuário sem conhecer esta constante, não descriptografa.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Tempus.TokenStore.v1");

    private readonly string _directory;

    public DpapiDataStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public Task StoreAsync<T>(string key, T value)
    {
        var json = NewtonsoftJsonSerializer.Instance.Serialize(value);
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.CurrentUser);

        File.WriteAllBytes(PathFor<T>(key), protectedBytes);
        return Task.CompletedTask;
    }

    public Task<T> GetAsync<T>(string key)
    {
        var path = PathFor<T>(key);
        if (!File.Exists(path)) return Task.FromResult<T>(default!);

        try
        {
            var plain = ProtectedData.Unprotect(
                File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);

            return Task.FromResult(
                NewtonsoftJsonSerializer.Instance.Deserialize<T>(Encoding.UTF8.GetString(plain)));
        }
        catch (Exception)
        {
            // Token ilegível — perfil do Windows recriado, arquivo corrompido, cópia de outra
            // máquina. Tratar como ausente leva ao estado Offline e ao re-consent, que é o
            // caminho correto. Falhar aqui deixaria o app sem saída.
            return Task.FromResult<T>(default!);
        }
    }

    public Task DeleteAsync<T>(string key)
    {
        var path = PathFor<T>(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        if (Directory.Exists(_directory))
            foreach (var file in Directory.EnumerateFiles(_directory, "*.dpapi"))
                File.Delete(file);

        return Task.CompletedTask;
    }

    private string PathFor<T>(string key)
    {
        var name = $"{typeof(T).FullName}-{key}";
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');

        return Path.Combine(_directory, $"{name}.dpapi");
    }
}
