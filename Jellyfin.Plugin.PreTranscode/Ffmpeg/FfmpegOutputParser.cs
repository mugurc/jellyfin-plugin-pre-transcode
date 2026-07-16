using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.PreTranscode.Ffmpeg;

/// <summary>
/// Pure, side-effect-free parsers that turn raw ffmpeg text output into structured capability
/// data. Kept separate from process execution so the parsing logic is directly unit-testable
/// against captured ffmpeg output.
/// </summary>
internal static partial class FfmpegOutputParser
{
    [GeneratedRegex(@"^\s*(?<flags>[A-Z.]{6})\s+(?<name>[A-Za-z0-9_]+)\s+(?<desc>.*)$")]
    private static partial Regex CodecLineRegex();

    [GeneratedRegex(@"^\s*E\s+(?<name>[A-Za-z0-9_]+)\s+(?<desc>.*)$")]
    private static partial Regex MuxerLineRegex();

    [GeneratedRegex(@"\(encoders:\s*(?<list>[^)]*)\)")]
    private static partial Regex EncodersGroupRegex();

    [GeneratedRegex(@"\((?:encoders|decoders):[^)]*\)")]
    private static partial Regex StripGroupsRegex();

    [GeneratedRegex(@"^\s*-preset\s+<(?<type>[a-z]+)>")]
    private static partial Regex PresetOptionRegex();

    [GeneratedRegex(@"\(from\s+(?<min>-?\d+)\s+to\s+(?<max>-?\d+)\)")]
    private static partial Regex RangeRegex();

    [GeneratedRegex(@"\(default\s+(?<def>[^)]*)\)")]
    private static partial Regex DefaultRegex();

    [GeneratedRegex(@"^\s+(?<name>[a-z0-9_]+)\s+(?<val>-?\d+)\b")]
    private static partial Regex ConstantLineRegex();

    [GeneratedRegex(@"^\s*-[A-Za-z0-9_]+\s")]
    private static partial Regex OptionLineRegex();

    [GeneratedRegex(@"^[a-z0-9_]+\s+<[a-z]+>")]
    private static partial Regex NewOptionRegex();

    /// <summary>
    /// Parses the output of <c>ffmpeg -codecs</c> into the encodable video/audio codecs and their encoders.
    /// </summary>
    /// <param name="codecsOutput">Raw <c>ffmpeg -codecs</c> output.</param>
    /// <returns>The parsed codecs.</returns>
    public static IReadOnlyList<CodecCapability> ParseCodecs(string codecsOutput)
    {
        var result = new List<CodecCapability>();
        if (string.IsNullOrEmpty(codecsOutput))
        {
            return result;
        }

        foreach (var line in SplitLines(codecsOutput))
        {
            var match = CodecLineRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var flags = match.Groups["flags"].Value;
            var name = match.Groups["name"].Value;
            var desc = match.Groups["desc"].Value;

            // flags layout: [0]=decode(D) [1]=encode(E) [2]=type(V/A/S).
            var canEncode = flags.Length > 1 && flags[1] == 'E';
            if (!canEncode)
            {
                continue;
            }

            var mediaType = flags.Length > 2 ? MapType(flags[2]) : CodecMediaType.Other;
            if (mediaType != CodecMediaType.Video && mediaType != CodecMediaType.Audio)
            {
                continue;
            }

            var encoderNames = ExtractEncoders(desc);
            if (encoderNames.Count == 0)
            {
                // ffmpeg omits the "(encoders: ...)" group when the encoder shares the codec name.
                encoderNames.Add(name);
            }

            var encoders = new List<EncoderCapability>();
            foreach (var encoderName in encoderNames)
            {
                encoders.Add(new EncoderCapability
                {
                    Name = encoderName,
                    IsHardware = IsHardwareEncoder(encoderName)
                });
            }

            result.Add(new CodecCapability
            {
                Name = name,
                Description = CleanDescription(desc),
                MediaType = mediaType,
                Encoders = encoders
            });
        }

        return result;
    }

    /// <summary>
    /// Parses the output of <c>ffmpeg -muxers</c> into available output containers.
    /// </summary>
    /// <param name="muxersOutput">Raw <c>ffmpeg -muxers</c> output.</param>
    /// <returns>The parsed containers.</returns>
    public static IReadOnlyList<ContainerCapability> ParseMuxers(string muxersOutput)
    {
        var result = new List<ContainerCapability>();
        if (string.IsNullOrEmpty(muxersOutput))
        {
            return result;
        }

        foreach (var line in SplitLines(muxersOutput))
        {
            var match = MuxerLineRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            result.Add(new ContainerCapability
            {
                Name = match.Groups["name"].Value,
                Description = match.Groups["desc"].Value.Trim()
            });
        }

        return result;
    }

    /// <summary>
    /// Parses the output of <c>ffmpeg -h filter=tonemap</c> into the available tone-mapping algorithms.
    /// </summary>
    /// <param name="filterHelp">Raw <c>ffmpeg -h filter=tonemap</c> output.</param>
    /// <returns>The available algorithm names (always includes <c>none</c>).</returns>
    public static IReadOnlyList<string> ParseTonemapModes(string filterHelp)
    {
        var modes = new List<string> { "none" };
        if (string.IsNullOrEmpty(filterHelp))
        {
            return modes;
        }

        var collecting = false;
        foreach (var line in SplitLines(filterHelp))
        {
            var trimmed = line.TrimStart();
            if (!collecting)
            {
                if (trimmed.StartsWith("tonemap", StringComparison.Ordinal)
                    && trimmed.Contains("<int>", StringComparison.Ordinal))
                {
                    collecting = true;
                }

                continue;
            }

            if (NewOptionRegex().IsMatch(trimmed))
            {
                break;
            }

            var constant = ConstantLineRegex().Match(line);
            if (constant.Success)
            {
                var name = constant.Groups["name"].Value;
                if (!modes.Contains(name))
                {
                    modes.Add(name);
                }
            }
        }

        return modes;
    }

    /// <summary>
    /// Parses the output of <c>ffmpeg -h encoder=NAME</c> to discover the encoder's valid preset values.
    /// </summary>
    /// <param name="encoderHelp">Raw <c>ffmpeg -h encoder=NAME</c> output.</param>
    /// <param name="encoder">The encoder name.</param>
    /// <returns>The discovered preset information (<see cref="PresetKind.None"/> if ffmpeg does not enumerate any).</returns>
    public static EncoderPresetInfo ParseEncoderPresets(string encoderHelp, string encoder)
    {
        var info = new EncoderPresetInfo { Encoder = encoder };
        if (string.IsNullOrEmpty(encoderHelp))
        {
            return info;
        }

        var lines = SplitLines(encoderHelp);
        for (var i = 0; i < lines.Length; i++)
        {
            var option = PresetOptionRegex().Match(lines[i]);
            if (!option.Success)
            {
                continue;
            }

            var type = option.Groups["type"].Value;
            var range = RangeRegex().Match(lines[i]);
            var def = DefaultRegex().Match(lines[i]);
            if (def.Success)
            {
                info.Default = def.Groups["def"].Value.Trim('"', ' ');
            }

            var values = new List<string>();
            for (var j = i + 1; j < lines.Length; j++)
            {
                if (OptionLineRegex().IsMatch(lines[j]))
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(lines[j]))
                {
                    if (values.Count > 0)
                    {
                        break;
                    }

                    continue;
                }

                var constant = ConstantLineRegex().Match(lines[j]);
                if (constant.Success)
                {
                    values.Add(constant.Groups["name"].Value);
                }
                else
                {
                    break;
                }
            }

            if (values.Count > 0)
            {
                info.Kind = PresetKind.NamedList;
                info.Values = values;
                info.FromFfmpeg = true;
            }
            else if (string.Equals(type, "int", StringComparison.Ordinal) && range.Success)
            {
                info.Kind = PresetKind.IntRange;
                info.FromFfmpeg = true;
                info.RangeMin = int.Parse(range.Groups["min"].Value, CultureInfo.InvariantCulture);
                info.RangeMax = int.Parse(range.Groups["max"].Value, CultureInfo.InvariantCulture);
            }
            else
            {
                info.Kind = PresetKind.None;
            }

            return info;
        }

        return info;
    }

    private static List<string> ExtractEncoders(string description)
    {
        var list = new List<string>();
        var match = EncodersGroupRegex().Match(description);
        if (!match.Success)
        {
            return list;
        }

        foreach (var token in match.Groups["list"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            list.Add(token);
        }

        return list;
    }

    private static string CleanDescription(string description)
    {
        return StripGroupsRegex().Replace(description, string.Empty).Trim();
    }

    private static bool IsHardwareEncoder(string encoderName)
    {
        string[] hardwareMarkers =
        {
            "_nvenc", "_qsv", "_vaapi", "_videotoolbox", "_amf", "_mf",
            "_v4l2m2m", "_d3d12va", "_cuvid", "_omx", "_mediacodec", "_rkmpp"
        };

        foreach (var marker in hardwareMarkers)
        {
            if (encoderName.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static CodecMediaType MapType(char typeFlag) => typeFlag switch
    {
        'V' => CodecMediaType.Video,
        'A' => CodecMediaType.Audio,
        'S' => CodecMediaType.Subtitle,
        _ => CodecMediaType.Other
    };

    private static string[] SplitLines(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    }
}
