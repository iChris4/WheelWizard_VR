using System.IO.Abstractions;
using System.Text;
using WheelWizard.Settings.Types;

namespace WheelWizard.Recomp;

/// <summary>Edits individual runtime-owned TOML keys without replacing unrelated user settings.</summary>
public static class RecompConfig
{
    private static string Code(string line)
    {
        bool single = false,
            quoted = false,
            escaped = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted && c == '\\' && !escaped)
            {
                escaped = true;
                continue;
            }
            if (c == '\'' && !quoted)
                single = !single;
            else if (c == '"' && !single && !escaped)
                quoted = !quoted;
            else if (c == '#' && !single && !quoted)
                return line[..i].Trim();
            escaped = false;
        }
        return line.Trim();
    }

    public static string? ReadString(IFileSystem fs, string path, string section, string key)
    {
        var value = ReadLiteral(fs, path, section, key);
        if (value is null)
            return null;
        if (value.Length < 2 || (value[0] != '"' && value[0] != '\'') || value[^1] != value[0])
            throw new InvalidDataException($"Invalid {section}.{key} in {path}.");
        var setting = new RecompSetting(typeof(string), (section, key), "", _ => { });
        setting.SetFromString(value, skipSave: true);
        return (string)setting.Get();
    }

    private static string? ReadLiteral(IFileSystem fs, string path, string section, string key)
    {
        if (!fs.File.Exists(path))
            return null;
        var active = false;
        foreach (var line in fs.File.ReadAllLines(path))
        {
            var code = Code(line);
            if (code.StartsWith('['))
            {
                active = code == $"[{section}]";
                continue;
            }
            var equals = code.IndexOf('=');
            if (!active || equals < 0 || code[..equals].Trim() != key)
                continue;
            return code[(equals + 1)..].Trim();
        }
        return null;
    }

    public static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public static void Set(IFileSystem fs, string path, string section, string key, string literal)
    {
        var lines = fs.File.Exists(path) ? fs.File.ReadAllLines(path).ToList() : new List<string>();
        var header = $"[{section}]";
        var start = lines.FindIndex(line => Code(line) == header);
        if (start < 0)
        {
            lines.Add("");
            lines.Add(header);
            lines.Add($"{key} = {literal}");
        }
        else
        {
            var end = start + 1;
            while (end < lines.Count && !Code(lines[end]).StartsWith('['))
                end++;
            var index = lines.FindIndex(
                start + 1,
                end - start - 1,
                line =>
                {
                    var code = Code(line);
                    var equals = code.IndexOf('=');
                    return equals >= 0 && code[..equals].Trim() == key;
                }
            );
            if (index >= 0)
                lines[index] = $"{key} = {literal}";
            else
                lines.Insert(end, $"{key} = {literal}");
        }
        WriteAtomic(fs, path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    public static void WriteAtomic(IFileSystem fs, string path, string text)
    {
        fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            fs.File.WriteAllText(temporary, text, new UTF8Encoding(false));
            fs.File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (fs.File.Exists(temporary))
                fs.File.Delete(temporary);
        }
    }

    public static void PrepareVr(IFileSystem fs, string normalConfig, string vrConfig)
    {
        var marker = fs.Path.Combine(fs.Path.GetDirectoryName(vrConfig)!, ".controller-imported");
        if (!fs.File.Exists(marker))
        {
            var target = fs.File.Exists(vrConfig) ? fs.File.ReadAllText(vrConfig) : "";
            var hasController = target.Split('\n').Any(line => Code(line) == "[controller]");
            if (!hasController && fs.File.Exists(normalConfig))
            {
                var block = new List<string>();
                var active = false;
                foreach (var line in fs.File.ReadAllLines(normalConfig))
                {
                    var code = Code(line);
                    if (code.StartsWith('['))
                        active = code == "[controller]";
                    if (active)
                        block.Add(line);
                }
                if (block.Count > 0)
                    WriteAtomic(fs, vrConfig, target.TrimEnd() + "\n\n" + string.Join('\n', block) + "\n");
            }
            fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(marker)!);
            fs.File.WriteAllText(marker, "Controller preferences are independent after first setup.\n");
        }
        if (ReadLiteral(fs, vrConfig, "vr", "enabled") is null)
            Set(fs, vrConfig, "vr", "enabled", "true");
        Set(fs, vrConfig, "vr", "required", "false");
        Set(fs, vrConfig, "video", "graphics_api", "\"d3d12\"");
    }
}
