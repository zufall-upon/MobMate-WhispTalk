#!/usr/bin/python3
"""Piperの実sourceでvoice言語の優先順位と設定変更を検証する。"""
import argparse
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument('--run-dir', required=True)
parser.add_argument('--dotnet', default='dotnet', help='Path to the .NET 8 SDK executable')
parser.add_argument('--voice-model', type=Path, help='任意の実voice。省略時は自己完結fixture')
args = parser.parse_args()
DOTNET = args.dotnet
run = Path(args.run_dir).resolve()
assert run.is_relative_to(ROOT / 'temp')
run.mkdir(parents=True, exist_ok=False)
envdir = run / '.sortie-env'
envdir.mkdir()
if args.voice_model is None:
    voice_dir = run / 'voice'
    voice_dir.mkdir()
    voice = voice_dir / 'fixture.onnx'
    voice.write_bytes(b'fixture')
    (voice_dir / 'config.json').write_text('{"language":{"code":"ja-en-zh-es-fr-pt"},"phoneme_type":"multilingual"}\n')
else:
    voice = args.voice_model.resolve()
    assert voice.is_file()
project = run / 'PiperLanguageRegression.csproj'
project.write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup>
    <Compile Include="{ROOT}/src/MobMate.Core/Tts/PiperPlusTtsService.cs" Link="PiperPlusTtsService.cs" />
    <Compile Include="{ROOT}/src/MobMate.Core/Tts/PiperPlusPersistentSessionManager.cs" Link="PiperPlusPersistentSessionManager.cs" />
    <Compile Include="{ROOT}/src/MobMate.Core/Tts/TtsProsodyAnalyzer.cs" Link="TtsProsodyAnalyzer.cs" />
  </ItemGroup>
</Project>\n''')
(run / 'NuGet.Config').write_text('<configuration><packageSources><clear /></packageSources></configuration>\n')
(run / 'Program.cs').write_text(r'''
using System.Reflection;
using MobMate.Core.Tts;

static List<string> Args(PiperPlusTtsService service, string method) =>
    (List<string>)typeof(PiperPlusTtsService).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
        .Invoke(service, method == "BuildRawArguments" ? new object?[] { "text", null } : new object?[] { "text", "out.wav", null })!;

static void RequireTag(PiperPlusTtsService service, string expected, string label)
{
    foreach (var method in new[] { "BuildRawArguments", "BuildArguments" })
    {
        var args = Args(service, method);
        var i = args.IndexOf("--language");
        var actual = i < 0 ? "" : args[i + 1];
        if (actual != expected) throw new InvalidOperationException($"{label}/{method}: expected [{expected}], got [{actual}]");
    }
}

var actualVoice = args[0];
var fixtureRoot = Path.Combine(args[1], "fixtures-" + Guid.NewGuid().ToString("N"));
using var service = new PiperPlusTtsService("/unused/PiperPlus.Cli", actualVoice);
service.SetConfig(reflectEmotion: false);
RequireTag(service, "ja-en-zh-es-fr-pt", "multilingual voice");

string Fixture(string name, string config, string? properties = null)
{
    var dir = Path.Combine(fixtureRoot, name);
    Directory.CreateDirectory(dir);
    var model = Path.Combine(dir, "model.onnx");
    File.WriteAllText(model, "fixture");
    File.WriteAllText(Path.Combine(dir, "config.json"), config);
    if (properties is not null) File.WriteAllText(Path.Combine(dir, "mobmate-model.properties"), properties);
    return model;
}
const string multilingual = "{\"language\":{\"code\":\"ja-en\"},\"phoneme_type\":\"multilingual\",\"audio\":{\"sample_rate\":22050}}";
var explicitText = Fixture("explicit-text", multilingual, "language=fr\ntext_mode_language_tag=ko\n");
var explicitLang = Fixture("explicit-language", multilingual, "language=fr\n");
var configured = Fixture("configured", multilingual);
var unknown = Fixture("unknown", "{\"language\":{\"code\":\"ja-unknown\"},\"phoneme_type\":\"multilingual\"}");
var single = Fixture("single", "{\"language\":{\"code\":\"en\"},\"phoneme_type\":\"multilingual\"}");
var other = Fixture("other", "{\"language\":{\"code\":\"ja-en\"},\"phoneme_type\":\"espeak\"}");
var malformed = Fixture("malformed", "{ invalid JSON");
foreach (var c in new[] { (explicitText, "ko"), (explicitLang, "fr"), (configured, "ja-en"),
                          (unknown, ""), (single, ""), (other, ""), (malformed, ""), (configured, "ja-en") })
{
    service.SetConfig(modelPath: c.Item1);
    RequireTag(service, c.Item2, c.Item1);
}
var overridePath = Path.Combine(fixtureRoot, "override.json");
File.WriteAllText(overridePath, "{\"language\":{\"code\":\"en-fr\"},\"phoneme_type\":\"multilingual\"}");
service.SetConfig(configPath: overridePath);
RequireTag(service, "en-fr", "configured config override");
service.SetConfig(modelPath: explicitText);
RequireTag(service, "ko", "model properties override config override");

if (OperatingSystem.IsLinux())
{
    var exe = Path.Combine(fixtureRoot, "unsupported-cli");
    var log = Path.Combine(fixtureRoot, "unsupported-cli.log");
    File.WriteAllText(exe, "#!/bin/sh\nprintf '%s\\n' \"$*\" >> \"$PIPER_TEST_LOG\"\nprintf 'Unsupported language for --text mode\\n' >&2\nexit 2\n");
    File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    var previous = Environment.GetEnvironmentVariable("PIPER_TEST_LOG");
    try
    {
        Environment.SetEnvironmentVariable("PIPER_TEST_LOG", log);
        using var unsupported = new PiperPlusTtsService(exe, configured);
        unsupported.SetConfig(reflectEmotion: false);
        var failure = await unsupported.SynthesizeAsync("text");
        if (failure.Success || failure.Error is null || !failure.Error.Contains("Piper failed"))
            throw new InvalidOperationException("unsupported CLI was not reported as a failure");
        var calls = File.ReadAllLines(log);
        if (calls.Length != 2 || calls.Any(call => !call.Contains("--language ja-en")))
            throw new InvalidOperationException("unsupported CLI silently changed language");
    }
    finally { Environment.SetEnvironmentVariable("PIPER_TEST_LOG", previous); }
}
''')
env = dict(os.environ, DOTNET_CLI_HOME=str(envdir), DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',
           DOTNET_NOLOGO='1', DOTNET_CLI_TELEMETRY_OPTOUT='1', NUGET_PACKAGES=str(envdir / 'packages'))
subprocess.run([DOTNET, 'restore', str(project), '--configfile', str(run / 'NuGet.Config'), '-p:NuGetAudit=false'],
               cwd=run, env=env, check=True)
subprocess.run([DOTNET, 'run', '--project', str(project), '--no-restore', '--', str(voice), str(run)],
               cwd=run, env=env, check=True)
print('Piper language regression: PASS')
