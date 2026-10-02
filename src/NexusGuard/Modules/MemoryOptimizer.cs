using System.Diagnostics;
using System.Runtime.InteropServices;
using NexusGuard.Core;

namespace NexusGuard.Modules;

public sealed record MemoryResult(long FreedBytes, int ProcessesTrimmed, bool StandbyPurged, string Message);

/// <summary>
/// Otimização de memória: reduz os working sets dos processos e, com privilégios de administrador,
/// liberta a lista de espera (standby list) e a cache de arquivos do sistema.
/// </summary>
public static class MemoryOptimizer
{
    /// <summary>Processos que não devem ser tocados — o ganho é nulo e o risco de instabilidade é real.</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression", "smss", "csrss", "wininit",
        "services", "lsass", "winlogon", "fontdrvhost", "dwm", "audiodg", "MsMpEng", "SecurityHealthService"
    };

    private static long AvailablePhysical()
    {
        var ms = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        return Native.GlobalMemoryStatusEx(ref ms) ? (long)ms.ullAvailPhys : 0;
    }

    /// <summary>
    /// Liberta memória. Com <paramref name="aggressive"/> inclui tambem a lista de espera e a cache do sistema
    /// (requer administrador).
    /// </summary>
    public static async Task<MemoryResult> OptimizeAsync(bool aggressive = true, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var before = AvailablePhysical();
        progress?.Report("A reduzir o conjunto de trabalho dos processos...");

        var trimmed = await Task.Run(() => TrimWorkingSets(ct), ct).ConfigureAwait(false);

        var standby = false;
        if (aggressive)
        {
            progress?.Report("Liberando a lista de espera e a cache do sistema...");
            standby = await Task.Run(PurgeSystemCaches, ct).ConfigureAwait(false);
        }

        // Dar tempo ao gestor de memória para refletir as alteracoes.
        await Task.Delay(900, ct).ConfigureAwait(false);

        var after = AvailablePhysical();
        var freed = Math.Max(0, after - before);

        var msg = standby
            ? $"{Fmt.Bytes(freed)} liberados · {trimmed} processos otimizados · lista de espera limpa"
            : Fmt.IsAdmin
                ? $"{Fmt.Bytes(freed)} liberados · {trimmed} processos otimizados"
                : $"{Fmt.Bytes(freed)} liberados · {trimmed} processos otimizados (execute como administrador para limpar a lista de espera)";

        Logger.Ok("Memória", msg);
        return new MemoryResult(freed, trimmed, standby, msg);
    }

    private static int TrimWorkingSets(CancellationToken ct)
    {
        var count = 0;

        foreach (var p in Process.GetProcesses())
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (Protected.Contains(p.ProcessName)) continue;
                if (p.Id <= 4) continue;

                if (Native.EmptyWorkingSet(p.Handle)) count++;
            }
            catch
            {
                // Sem permissão para este processo: normal sem elevação.
            }
            finally
            {
                p.Dispose();
            }
        }

        return count;
    }

    /// <summary>Esvazia a lista de espera, a lista modificada e a cache de arquivos do sistema.</summary>
    private static bool PurgeSystemCaches()
    {
        if (!Fmt.IsAdmin) return false;

        var okProfile = Native.EnablePrivilege("SeProfileSingleProcessPrivilege");
        Native.EnablePrivilege("SeIncreaseQuotaPrivilege");

        if (!okProfile)
        {
            Logger.Warn("Memória", "Privilégio SeProfileSingleProcessPrivilege indisponível; lista de espera não limpa.");
            return false;
        }

        var any = false;
        any |= SetMemoryList(Native.MemoryFlushModifiedList);
        any |= SetMemoryList(Native.MemoryPurgeStandbyList);
        any |= SetMemoryList(Native.MemoryEmptyWorkingSets);
        any |= PurgeSystemFileCache();

        return any;
    }

    private static bool SetMemoryList(int command)
    {
        var buf = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(buf, command);
            var status = Native.NtSetSystemInformation(Native.SystemMemoryListInformation, buf, sizeof(int));
            if (status != 0)
                Logger.Warn("Memória", $"Comando de memória {command} devolveu NTSTATUS 0x{status:X8}.");
            return status == 0;
        }
        catch (Exception ex)
        {
            Logger.Warn("Memória", $"Comando de memória {command} falhou: {ex.Message}");
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>SYSTEM_FILECACHE_INFORMATION com flags de purga esvazia a cache de arquivos.</summary>
    private static bool PurgeSystemFileCache()
    {
        // Estrutura: nuint CurrentSize, PeakSize; uint PageFaultCount; nuint Min/MaxWorkingSet; nuint CurrentSizeIncludingTransitionInPages,
        // PeakSizeIncludingTransitionInPages; uint TransitionRePurposeCount, Flags.
        var size = IntPtr.Size == 8 ? 0x48 : 0x24;
        var buf = Marshal.AllocHGlobal(size);

        try
        {
            for (var i = 0; i < size; i++) Marshal.WriteByte(buf, i, 0);

            // MM_WORKING_SET_MAX_HARD_ENABLE | MM_WORKING_SET_MIN_HARD_ENABLE combinados com min/max = -1 forçam a purga.
            var minOffset = IntPtr.Size == 8 ? 0x18 : 0x0C;
            Marshal.WriteIntPtr(buf, minOffset, new IntPtr(-1));
            Marshal.WriteIntPtr(buf, minOffset + IntPtr.Size, new IntPtr(-1));

            var status = Native.NtSetSystemInformation(Native.SystemFileCacheInformation, buf, size);
            return status == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>Reduz o working set de um processo específico.</summary>
    public static bool TrimProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return Native.EmptyWorkingSet(p.Handle);
        }
        catch (Exception ex)
        {
            Logger.Warn("Memória", $"Não foi possível otimizar o PID {pid}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Termina um processo. Recusa-se a tocar nos processos críticos do sistema.</summary>
    public static bool KillProcess(int pid, out string message)
    {
        try
        {
            using var p = Process.GetProcessById(pid);

            if (Protected.Contains(p.ProcessName))
            {
                message = $"'{p.ProcessName}' é um processo crítico do Windows e não será terminado.";
                return false;
            }

            var name = p.ProcessName;
            p.Kill(entireProcessTree: true);
            message = $"Processo '{name}' (PID {pid}) terminado.";
            Logger.Ok("Processos", message);
            return true;
        }
        catch (Exception ex)
        {
            message = $"Não foi possível terminar o PID {pid}: {ex.Message}";
            Logger.Warn("Processos", message);
            return false;
        }
    }

    public static bool IsProtected(string processName) => Protected.Contains(processName);
}
