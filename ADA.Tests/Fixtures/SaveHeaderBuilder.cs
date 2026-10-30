using System.Text;

namespace ADA.Tests.Fixtures;

/// <summary>
/// Writes save headers in the game's 1.2 layout for reader tests, including the tail fields.
/// Defaults match a real autosave, with made-up account details.
/// </summary>
public sealed class SaveHeaderBuilder
{
    private int _headerVersion = 14;
    private string _saveName = "Atria_autosave_2";
    private string _mapOptions = "?skiponboarding?ClientIdentity=00ff00ff?FallbackAccountNames=00000000";
    private string _session = "Atria";
    private int _playSeconds = 600;
    private long _savedAtTicks = new DateTime(2026, 10, 25, 12, 53, 12, DateTimeKind.Utc).Ticks;
    private byte _visibility;
    private string _modMetadata = "";
    private bool _isModded;
    private string _saveIdentifier = "0bV9f0YUAB7qx-SKOn2I2Q";
    private bool _advancedGameSettings;
    private bool _utf16;
    private byte[] _worldData = [];

    public SaveHeaderBuilder HeaderVersion(int version)
    {
        _headerVersion = version;
        return this;
    }

    public SaveHeaderBuilder SaveName(string name)
    {
        _saveName = name;
        return this;
    }

    public SaveHeaderBuilder MapOptions(string options)
    {
        _mapOptions = options;
        return this;
    }

    public SaveHeaderBuilder Session(string session)
    {
        _session = session;
        return this;
    }

    public SaveHeaderBuilder PlaySeconds(int seconds)
    {
        _playSeconds = seconds;
        return this;
    }

    public SaveHeaderBuilder SavedAtTicks(long ticks)
    {
        _savedAtTicks = ticks;
        return this;
    }

    public SaveHeaderBuilder Visibility(byte visibility)
    {
        _visibility = visibility;
        return this;
    }

    /// <summary>Records this mod metadata JSON and marks the save modded.</summary>
    public SaveHeaderBuilder ModMetadata(string json)
    {
        _modMetadata = json;
        _isModded = true;
        return this;
    }

    public SaveHeaderBuilder AdvancedGameSettings(bool on = true)
    {
        _advancedGameSettings = on;
        return this;
    }

    /// <summary>Writes every string as UTF-16, as the game does when a string has non-Latin characters.</summary>
    public SaveHeaderBuilder Utf16Strings()
    {
        _utf16 = true;
        return this;
    }

    /// <summary>Bytes after the header, standing in for the compressed world.</summary>
    public SaveHeaderBuilder WorldData(byte[] bytes)
    {
        _worldData = bytes;
        return this;
    }

    public byte[] Build()
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.Latin1, leaveOpen: true))
        {
            writer.Write(_headerVersion);
            writer.Write(60);
            writer.Write(513885);
            if (_headerVersion >= 14)
            {
                WriteString(writer, _saveName);
            }
            WriteString(writer, "Persistent_Level");
            WriteString(writer, _mapOptions);
            WriteString(writer, _session);
            writer.Write(_playSeconds);
            writer.Write(_savedAtTicks);
            writer.Write(_visibility);
            writer.Write(40);
            WriteString(writer, _modMetadata);
            writer.Write(_isModded ? 1 : 0);
            WriteString(writer, _saveIdentifier);
            writer.Write(1);
            writer.Write(1);
            writer.Write(new byte[16]);
            writer.Write(_advancedGameSettings ? 1 : 0);
            writer.Write(_worldData);
        }
        return buffer.ToArray();
    }

    public MemoryStream ToStream() => new(Build());

    /// <summary>A session setting value as the game writes it: an Unreal variant, base64-encoded.</summary>
    public static string Variant(int typeTag, byte[] value) =>
        Convert.ToBase64String([.. BitConverter.GetBytes(typeTag), .. BitConverter.GetBytes(value.Length), .. value]);

    private void WriteString(BinaryWriter writer, string text)
    {
        if (text.Length == 0)
        {
            writer.Write(0);
            return;
        }

        if (_utf16)
        {
            writer.Write(-(text.Length + 1));
            writer.Write(Encoding.Unicode.GetBytes(text + '\0'));
        }
        else
        {
            var bytes = Encoding.Latin1.GetBytes(text + '\0');
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }
    }
}
