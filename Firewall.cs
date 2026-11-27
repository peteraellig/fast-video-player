using System.ComponentModel;
using System.Diagnostics;

namespace CuePlayer;

/// <summary>
/// Windows firewall rule for the remote HTTP port (Peter 2026-11-27: a button in the remote card).
/// Reading the rules needs no rights (COM HNetCfg.FwPolicy2); adding / removing runs netsh elevated (UAC question, the user answers it).
/// "Add" first deletes every inbound rule of this program - also the block rule Windows makes when someone answered its own
/// firewall question with "cancel" (block rules win over allow rules) - then adds one allow rule: TCP, the remote port, this exe, all profiles
/// (event networks are often "public").
/// </summary>
internal static class Firewall
{
    public const string RuleName = "fast video player (remote HTTP)";

    public sealed record State(bool Any, bool Allowed, string Text);

    private static string Exe => Environment.ProcessPath ?? Application.ExecutablePath;

    public static State Check(int port, string? exe = null)
    {
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type == null) return new State(false, false, "cannot be read on this PC");
            dynamic policy = Activator.CreateInstance(type)!;
            bool any = false, allowed = false, blocked = false;
            var otherPorts = new List<string>();
            foreach (dynamic r in policy.Rules)
            {
                string? app = r.ApplicationName;
                if (app == null || !string.Equals(Path.GetFullPath(app), Path.GetFullPath(exe ?? Exe), StringComparison.OrdinalIgnoreCase)) continue;
                if ((int)r.Direction != 1 || !(bool)r.Enabled) continue;   // 1 = inbound
                any = true;
                int proto = (int)r.Protocol;   // 6 = TCP, 256 = any
                string? lp = r.LocalPorts;
                string ports = string.IsNullOrWhiteSpace(lp) ? "*" : lp;   // empty for "any protocol" rules (measured with vMix64)
                bool portOk = ports == "*" || ports.Split(',').Any(p => PortIn(p.Trim(), port));
                if ((int)r.Action == 0) { if (proto is 6 or 256) blocked = true; }   // 0 = block
                else if (proto is 6 or 256 && portOk) allowed = true;
                else if (proto is 6 or 256) otherPorts.Add(ports);
            }
            if (blocked) return new State(any, false, "BLOCKED for this program (an earlier answer to the Windows question) - Add firewall rule replaces it");
            if (allowed) return new State(any, true, $"allowed, port {port}");
            if (otherPorts.Count > 0) return new State(any, false, $"rule for port {string.Join(", ", otherPorts)}, not {port} - update it");
            return new State(any, false, "no rule - other computers may not reach the player");
        }
        catch (Exception e)
        {
            Log.Write("firewall check: " + e.Message);
            return new State(false, false, "cannot be read (" + e.Message + ")");
        }
    }

    private static bool PortIn(string spec, int port)
    {
        int dash = spec.IndexOf('-');
        if (dash > 0) return int.TryParse(spec[..dash], out var a) && int.TryParse(spec[(dash + 1)..], out var b) && port >= a && port <= b;
        return int.TryParse(spec, out var p) && p == port;
    }

    /// <summary>Adds (replaces) the rule. Returns what to show: done / cancelled / failed.</summary>
    public static Task<string> AddAsync(int port) => RunElevated(
        $"netsh advfirewall firewall delete rule name=all dir=in program=\"{Exe}\" & " +
        $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={port} program=\"{Exe}\" profile=any enable=yes",
        $"rule added for port {port}");

    public static Task<string> RemoveAsync() => RunElevated(
        $"netsh advfirewall firewall delete rule name=all dir=in program=\"{Exe}\"",
        "rules removed");

    private static async Task<string> RunElevated(string commands, string done)
    {
        try
        {
            // cmd /s /c "...": /s only strips the outer quotes, the quoted paths inside stay intact
            var psi = new ProcessStartInfo("cmd.exe", $"/s /c \"{commands}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            if (p == null) return "failed: could not start netsh";
            await p.WaitForExitAsync();
            Log.Write($"firewall: {commands} -> exit {p.ExitCode}");
            return p.ExitCode == 0 ? done : $"failed (netsh exit {p.ExitCode})";
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)   // the user said no to the administrator question
        {
            return "cancelled";
        }
        catch (Exception e)
        {
            Log.Write("firewall: " + e.Message);
            return "failed: " + e.Message;
        }
    }
}
