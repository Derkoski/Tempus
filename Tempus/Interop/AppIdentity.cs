using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Tempus.Interop;

/// <summary>
/// A identidade do app perante o Windows: o <b>AppUserModelID</b>.
/// <para>
/// Existe por causa dos toasts. Um app <b>não empacotado</b> — que é o nosso caso, decidido no
/// D-001 — só consegue notificar se duas coisas concordarem: o processo declara um AUMID, e existe
/// um atalho no Menu Iniciar carregando o mesmo AUMID na propriedade
/// <c>System.AppUserModel.ID</c>. É desse atalho que o Windows tira o nome e o ícone que aparecem
/// no toast; sem ele a chamada não falha, ela simplesmente não mostra nada — que é o pior modo de
/// falhar e o motivo deste arquivo existir separado.
/// </para>
/// <para>
/// O atalho é <b>consertado, não recriado</b>: se o <c>install.ps1</c> já deixou um <c>Tempus.lnk</c>
/// ali (e ele deixa, via WScript.Shell, que não sabe gravar propriedades), este código só acrescenta
/// a propriedade que falta e preserva o alvo. Isso é o que permite rodar o build de desenvolvimento
/// sem sequestrar o atalho da instalação: o AUMID vale para a máquina, não para o caminho do exe.
/// </para>
/// </summary>
internal static class AppIdentity
{
    /// <summary>
    /// Convenção <c>Empresa.Produto</c>. <b>Não mudar</b>: trocar o AUMID órfã as notificações já
    /// entregues na Central de Ações e exige atalho novo.
    /// </summary>
    public const string Aumid = "Sponte.Tempus";

    private static readonly PropertyKey AppUserModelIdKey =
        new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    /// <summary>
    /// Por que o atalho não pôde ser preparado. Um diagnóstico que diz "falhou" sem dizer o motivo
    /// é meio diagnóstico — e esta é justamente a parte do app que falha de modo silencioso.
    /// </summary>
    public static string? LastError { get; private set; }

    /// <summary>
    /// Declara o AUMID do processo. Precisa acontecer <b>antes de qualquer janela</b>, senão o
    /// shell já terá agrupado o processo sob a identidade herdada de quem o lançou.
    /// </summary>
    public static void Declare()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(Aumid);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"AUMID não declarado: {e.Message}");
        }
    }

    /// <summary>
    /// Garante o atalho do Menu Iniciar com o AUMID. Devolve <c>false</c> quando não deu — e aí
    /// quem chama simplesmente não tenta notificar, em vez de emitir para o vazio.
    /// </summary>
    public static bool EnsureShortcut()
    {
        try
        {
            LastError = null;

            var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);

            if (string.IsNullOrEmpty(programs))
            {
                LastError = "Pasta Programas do Menu Iniciar não encontrada.";
                return false;
            }

            var path = Path.Combine(programs, "Tempus.lnk");

            return File.Exists(path) ? PatchExisting(path) : CreateNew(path);
        }
        catch (Exception e)
        {
            // Menu Iniciar bloqueado por política, perfil móvel estranho, disco cheio: nada disso
            // pode impedir a barra de subir. Sem toast é uma degradação; sem barra é uma falha.
            LastError = $"{e.GetType().Name}: {e.Message}";
            Debug.WriteLine($"Atalho do Menu Iniciar não pôde ser preparado: {LastError}");
            return false;
        }
    }

    /// <summary>Acrescenta o AUMID a um atalho que já existe, sem tocar no alvo nem no ícone.</summary>
    private static bool PatchExisting(string path)
    {
        var link = (IShellLinkW)new ShellLink();
        ((IPersistFile)link).Load(path, STGM_READWRITE);

        var store = (IPropertyStore)link;

        if (ReadAumid(store) == Aumid) return true;

        WriteAumid(store);
        ((IPersistFile)link).Save(null, fRemember: true);
        return true;
    }

    private static bool CreateNew(string path)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        var link = (IShellLinkW)new ShellLink();
        link.SetPath(exe);
        link.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? string.Empty);
        link.SetDescription("Tempus - barra de agenda e tarefas");

        WriteAumid((IPropertyStore)link);
        ((IPersistFile)link).Save(path, fRemember: true);
        return true;
    }

    private static string? ReadAumid(IPropertyStore store)
    {
        var key = AppUserModelIdKey;
        store.GetValue(ref key, out var value);

        try
        {
            return value.vt == VT_LPWSTR ? Marshal.PtrToStringUni(value.data) : null;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    /// <summary>
    /// Monta o <c>PROPVARIANT</c> na mão. O caminho óbvio seria
    /// <c>InitPropVariantFromString</c>, mas ele é <b>inline no propvarutil.h</b> e não um export
    /// de verdade — P/Invoke para ele falha com <c>EntryPointNotFoundException</c> em tempo de
    /// execução. Aqui se faz o que aquele helper faria: string em memória COM e VT_LPWSTR no
    /// discriminante, para o <c>PropVariantClear</c> liberar corretamente depois.
    /// </summary>
    private static void WriteAumid(IPropertyStore store)
    {
        var key = AppUserModelIdKey;
        var value = new PropVariant { vt = VT_LPWSTR, data = Marshal.StringToCoTaskMemUni(Aumid) };

        try
        {
            store.SetValue(ref key, ref value);
            store.Commit();
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    // ---------------------------------------------------------------- interop

    private const uint STGM_READWRITE = 0x0000_0002;
    private const ushort VT_LPWSTR = 31;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    /// <summary>
    /// Só o suficiente de <c>PROPVARIANT</c> para uma string: 24 bytes em x64, dos quais usamos o
    /// discriminante e o ponteiro. Quem aloca é o <c>InitPropVariantFromString</c> e quem libera é
    /// o <c>PropVariantClear</c> — nunca soltar um destes sem passar pelo segundo.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort reserved1;
        public ushort reserved2;
        public ushort reserved3;
        public IntPtr data;
        public IntPtr padding;
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    /// <summary>
    /// A ordem dos métodos <b>é</b> o contrato: COM chama por posição na vtable, não por nome.
    /// Os que não usamos ficam aqui só para não deslocar os que usamos.
    /// </summary>
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch,
            IntPtr findData, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out ushort hotkey);
        void SetHotkey(ushort hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder icon, int cch,
            out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relative, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? fileName,
            [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }
}
