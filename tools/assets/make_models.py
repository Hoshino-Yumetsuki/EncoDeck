# Generates src/EncoDeck.Protocol/Oppo/Models/OppoCatalog.g.cs from the official client's model
# configuration (the "compatWhiteList" of HeyMelody's whitelist file, read locally for
# interoperability). Only the facts EncoDeck needs to talk to each model are written: product id,
# names, control UUID, the noise-control tree, EQ presets, gesture rows and feature flags.
# Usage: python -I make_models.py <whitelist.json> <output.cs>
import json
import sys
from pathlib import Path

# Integer flags EncoDeck evaluates (see OppoCatalog.cs for what each one drives).
INT_FLAGS = [
    "equalizer", "customEqualizer", "customEqMax", "customEqUiVersion", "gameMode", "wearDetection",
    "multiDevicesConnect", "sleepDetection", "swiftPair", "voiceCommand", "headMotion",
    "highToneQuality", "highAudio", "spatialHiResMutex", "bassEngineSupport", "promptVolume",
    "promptVolumePercent", "heyFindDevice", "findDevice", "fitDetection", "utcTimeZone",
    "sendHostCapabilities", "zenMode", "longPressType", "supportPinch", "noiseReductionUIVersion",
    "spatialDescriptionType", "batteryRadix", "incomingCallControl", "longPressVolume", "tapLevelSetting",
    # Read only to build the official 0x010D status query.
    "hearingEnhancement", "hearingEnhancementNew", "personalNoise", "clickTakePic", "clickTakePicNew",
    "vocalEnhance", "headSetSoundRecord", "longPowerMode", "smartCall", "deviceLostRemind", "voiceWake",
    "controlAutoVolumeSupport", "collectLogs", "spineHealth", "adaptiveVolume", "adaptiveEar",
    "speechPerception", "meetingAssistant", "hearingOptimize", "gmnVoiceAssist", "opsReduction",
]
INT_LISTS = ["spatialTypes", "customEqFrequency", "gameSoundMutexes"]


def clean_name(name: str) -> str:
    # A few names in the source carry a mangled regional suffix; keep the readable part.
    if "�" in name:
        name = name[: name.index("�")]
    return name.strip()


def cs(text: str) -> str:
    return '"' + text.replace("\\", "\\\\").replace('"', '\\"') + '"'


def ints(values) -> str:
    return "[" + ", ".join(str(int(v)) for v in values) + "]"


def noise(node) -> str:
    children = ", ".join(noise(c) for c in node.get("childrenMode") or [])
    decided = ", DecidedByDevice = true" if node.get("decideByEarDevice") else ""
    return f"new({node['modeType']}, {node['protocolIndex']}, [{children}]) {{ {decided.lstrip(', ')} }}" if decided else f"new({node['modeType']}, {node['protocolIndex']}, [{children}])"


def eq(entry) -> str:
    return (f"new({entry['modeType']}, {entry['protocolIndex']}, {entry.get('order', 0)}, "
            f"{entry.get('minFirmVersion', 0)}, {entry.get('colorId', -1)})")


def control(entry) -> str:
    return (f"new({entry.get('action', 0)}, {entry.get('support', 0)}, {entry.get('showEar', 0)}, "
            f"{entry.get('defaultFunction', 0)}, {entry.get('minSelectCount', 0)})")


def model(m) -> str:
    f = m.get("function") or {}
    names = [clean_name(m["name"])] + [clean_name(c["name"]) for c in m.get("children") or [] if c.get("name")]
    values = {k: int(f[k]) for k in INT_FLAGS if isinstance(f.get(k), (int, bool))}
    # Presence-only facts, stored as flags.
    if f.get("gameModeList") is not None:
        values["gameModeList"] = 1
    if f.get("gameEqPkgList"):
        values["gameEqPkgList"] = 1
    if (f.get("personalNoiseCompat") or {}).get("personalNoise") == 1:
        values["personalNoiseCompat"] = 1
    flags = ", ".join(f"[{cs(k)}] = {v}" for k, v in values.items())
    lists = ", ".join(f"[{cs(k)}] = {ints(f[k])}" for k in INT_LISTS if isinstance(f.get(k), list))
    prompt = f.get("promptVolumeRange") or {}
    lines = [
        f"        new({cs(m['id'].upper())}, {cs(m.get('brand') or '')}, {cs(m.get('type') or '')}, [{', '.join(cs(n) for n in dict.fromkeys(names))}], {cs((m.get('uuid') or '').upper())})",
        "        {",
        f"            Noise = [{', '.join(noise(n) for n in f.get('noiseReductionMode') or [])}],",
        f"            Equalizer = [{', '.join(eq(e) for e in f.get('equalizerMode') or [])}],",
        f"            EqualizerCompat = [{', '.join(eq(e) for e in f.get('equalizerModeCompat') or [])}],",
        f"            EqualizerByVersion = [{', '.join(eq(e) for e in f.get('equalizerModeByVersion') or [])}],",
        f"            Controls = [{', '.join(control(c) for c in f.get('control') or [])}],",
        f"            CallControls = [{', '.join(control(c) for c in f.get('callControl') or [])}],",
        f"            GameSoundTypes = {ints(g.get('type', 0) for g in f.get('gameSoundList') or [])},",
        f"            MultiConnectFunctions = [{', '.join(cs(x['functionType']) for x in f.get('multiConnectFunctions') or [] if x.get('functionType'))}],",
        f"            PromptVolumeRange = {('(' + str(prompt['min']) + ', ' + str(prompt['max']) + ')') if 'min' in prompt and 'max' in prompt else 'null'},",
        f"            Flags = new Dictionary<string, int> {{ {flags} }},",
        f"            Lists = new Dictionary<string, int[]> {{ {lists} }},",
        "        },",
    ]
    return "\n".join(lines)


def main() -> None:
    source, output = Path(sys.argv[1]), Path(sys.argv[2])
    data = json.loads(source.read_text(encoding="utf-8"))
    models = sorted(data["compatWhiteList"], key=lambda m: m["id"])
    body = "\n".join(model(m) for m in models if m.get("supportSpp", True))
    text = f"""// SPDX-License-Identifier: GPL-3.0-only
// <auto-generated>
// Generated by tools/assets/make_models.py from the official client's model configuration
// (interoperability facts only). Do not edit by hand; re-run the script instead.
// </auto-generated>
namespace EncoDeck.Protocol.Oppo.Models;

internal static partial class OppoCatalog
{{
    /// <summary>Configuration version the catalog was generated from.</summary>
    public const long SourceVersion = {int(data.get('compatVersionCode') or 0)};

    internal static IReadOnlyList<CatalogModel> Models {{ get; }} =
    [
{body}
    ];
}}
"""
    output.write_text(text, encoding="utf-8", newline="\n")
    print("wrote", output, len(models), "models")


if __name__ == "__main__":
    main()
