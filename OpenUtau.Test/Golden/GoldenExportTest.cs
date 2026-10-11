using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NAudio.Wave;
using OpenUtau.Api;
using OpenUtau.Classic;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;
using Xunit;

namespace OpenUtau.Core.Golden {
    /// <summary>
    /// Exports intermediate data (golden data) for the Flutter port.
    /// Does nothing unless GOLDEN_OUT is set, so the normal test run is unaffected.
    ///
    ///   GOLDEN_UST=path/to/main.ust GOLDEN_SINGER=path/to/voicebank GOLDEN_OUT=path/to/golden/case \
    ///     OpenUtau.Test/bin/Debug/net10.0/OpenUtau.Test -class OpenUtau.Core.Golden.GoldenExportTest
    ///
    /// Outputs (renderer is fixed to WORLDLINE-R; the user's OpenUtau prefs and presets are not read):
    ///   voicebank.json       the singer as ClassicSinger loads it: info, subbanks, otos and oto lookups (stage 1a)
    ///   ust.json             Ust.Load(reader, file) result, before AfterLoad/Validate (stage 0)
    ///   input.json           notes after Ust.Load(files), i.e. after AfterLoad and ValidateFull (stage 0)
    ///   phonemizer.json      validated notes, the note groups sent to the phonemizer and its raw output (stage 1b)
    ///   saved.ust            Ust.SavePart after phonemizing, Shift_JIS; VoiceDir / CacheDir values replaced by
    ///                        <singer> / <cache> since they are machine specific
    ///   phonemes.json        phonemizer output after timing validation (stage 1)
    ///   phrases.json         RenderPhrase.FromPart output (stage 2)
    ///   resampler_items.json ResamplerItem per phone, and the PhraseSynthV2.AddRequest arguments (stage 3)
    ///   synth_curves.json    per-frame curves passed to PhraseSynthV2.SetCurves (stage 3)
    ///   phrase_N.wav         WorldlineRenderer output per phrase, 44.1kHz mono float32 (stage 4)
    /// </summary>
    public class GoldenExportTest {
        readonly ITestOutputHelper output;

        public GoldenExportTest(ITestOutputHelper output) {
            this.output = output;
        }

        static readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        [Fact]
        public void Export() {
            var outDir = Environment.GetEnvironmentVariable("GOLDEN_OUT");
            if (string.IsNullOrEmpty(outDir)) {
                output.WriteLine("GOLDEN_OUT is not set. Skipped.");
                return;
            }
            var ustPath = Environment.GetEnvironmentVariable("GOLDEN_UST");
            var singerDir = Environment.GetEnvironmentVariable("GOLDEN_SINGER");
            Assert.True(File.Exists(ustPath), $"GOLDEN_UST not found: {ustPath}");
            Assert.True(Directory.Exists(singerDir), $"GOLDEN_SINGER not found: {singerDir}");
            Directory.CreateDirectory(outDir);

            // Must come first: Preferences and NotePresets load from DataPath on first use.
            var tempDir = UseTempDataDirs();
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            UseWorldlineFromRepo();
            // Registers the built-in resamplers (worldline) that ResamplerItem looks up.
            ToolsManager.Inst.Initialize();
            try {
                var singer = LoadSinger(singerDir);
                Write(outDir, "voicebank.json", ExportVoicebank(singer, singerDir));

                using (var reader = new StreamReader(ustPath, Ust.DetectEncoding(ustPath))) {
                    Write(outDir, "ust.json", ExportUst(Ust.Load(reader, ustPath)));
                }

                var project = Ust.Load(new[] { ustPath });
                var track = project.tracks[0];
                var part = project.parts.OfType<UVoicePart>().First();
                track.Singer = singer;
                track.RendererSettings.renderer = Renderers.WORLDLINE_R;

                Phonemize(project, part);
                Write(outDir, "phonemizer.json", ExportPhonemizer(track, part));
                ExportSavedUst(project, part, Path.Join(outDir, "saved.ust"), tempDir);
                var phrases = RenderPhrase.FromPart(project, track, part);

                Write(outDir, "input.json", ExportInput(project, part, ustPath, singerDir));
                Write(outDir, "phonemes.json", ExportPhonemes(part));
                Write(outDir, "phrases.json", phrases.Select(ph => ExportPhrase(ph, part.position)).ToList());

                var renderer = new WorldlineRenderer(version: 10);
                Assert.Equal(Renderers.WORLDLINE_R, track.RendererSettings.Renderer.ToString());
                var items = new List<object>();
                var curves = new List<object>();
                for (int i = 0; i < phrases.Count; i++) {
                    var phrase = phrases[i];
                    items.Add(phrase.phones.Select(phone => ExportResamplerItem(phrase, new ResamplerItem(phrase, phone))).ToList());
                    curves.Add(ExportSynthCurves(renderer, phrase));
                    var result = renderer.Render(phrase, new Progress(phrase.phones.Length), 0, new CancellationTokenSource(), false).Result;
                    WriteFloatWav(Path.Join(outDir, $"phrase_{i}.wav"), result.samples);
                }
                Write(outDir, "resampler_items.json", items);
                Write(outDir, "synth_curves.json", curves);
                output.WriteLine($"notes={part.notes.Count} phonemes={part.phonemes.Count} phrases={phrases.Count} -> {outDir}");
            } finally {
                Directory.Delete(tempDir, true);
            }
        }

        static bool worldlineResolverSet;

        /// <summary>
        /// The test project does not copy runtimes/ to its output, so resolve "worldline" from the repo's runtimes/.
        /// </summary>
        static void UseWorldlineFromRepo() {
            if (worldlineResolverSet || !OperatingSystem.IsMacOS()) {
                return;
            }
            var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(GoldenExportTest).Assembly.Location)!);
            while (dir != null && !File.Exists(Path.Join(dir.FullName, "runtimes", "osx", "native", "libworldline.dylib"))) {
                dir = dir.Parent;
            }
            Assert.True(dir != null, "runtimes/osx/native/libworldline.dylib not found.");
            var lib = Path.Join(dir!.FullName, "runtimes", "osx", "native", "libworldline.dylib");
            NativeLibrary.SetDllImportResolver(typeof(WorldlineRenderer).Assembly, (name, assembly, path) =>
                name == "worldline" ? NativeLibrary.Load(lib) : IntPtr.Zero);
            worldlineResolverSet = true;
        }

        /// <summary>
        /// Points DataPath and CachePath at an empty temp dir, so that the output does not depend on the
        /// OpenUtau app installed on this machine: its prefs.json, notepresets.json, resamplers and singers
        /// are not read, and a cached (16-bit) wav from a previous run is never read back.
        /// </summary>
        static string UseTempDataDirs() {
            var dir = Path.Join(Path.GetTempPath(), $"openutau-golden-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Join(dir, "Cache"));
            typeof(PathManager).GetProperty(nameof(PathManager.DataPath))!.SetValue(PathManager.Inst, dir);
            typeof(PathManager).GetProperty(nameof(PathManager.CachePath))!.SetValue(PathManager.Inst, Path.Join(dir, "Cache"));
            return dir;
        }

        static void WriteFloatWav(string path, float[] samples) {
            using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(44100, 1));
            writer.WriteSamples(samples, 0, samples.Length);
        }

        static ClassicSinger LoadSinger(string singerDir) {
            singerDir = Path.GetFullPath(singerDir);
            // Like the app: IsTest stays false, so CheckWavExist runs (it maps oto.ini names to NFD file names).
            VoicebankLoader.IsTest = false;
            var voicebank = new Voicebank() {
                File = Path.Join(singerDir, "character.txt"),
                BasePath = Path.GetDirectoryName(singerDir),
            };
            VoicebankLoader.LoadVoicebank(voicebank);
            var singer = new ClassicSinger(voicebank);
            singer.EnsureLoaded();
            return singer;
        }

        /// <summary>
        /// Runs the real PhonemizerRunner outside the UI loop and waits until the part has applied its response.
        /// </summary>
        static void Phonemize(UProject project, UVoicePart part) {
            typeof(DocManager).GetProperty(nameof(DocManager.Project))!.SetValue(DocManager.Inst, project);
            using var runner = new PhonemizerRunner(TaskScheduler.Default);
            DocManager.Inst.SetPhonemizerRunnerForTest(runner);
            try {
                project.ValidateFull();
                var sw = Stopwatch.StartNew();
                while (!part.PhonemesUpToDate) {
                    Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "Phonemizer timed out.");
                    Thread.Sleep(10);
                }
            } finally {
                DocManager.Inst.SetPhonemizerRunnerForTest(null);
            }
            project.Validate(new ValidateOptions { SkipPhonemizer = true });
        }

        static void Write(string dir, string name, object data) {
            File.WriteAllText(Path.Join(dir, name), JsonSerializer.Serialize(data, jsonOptions) + "\n");
        }

        static object ExportInput(UProject project, UVoicePart part, string ustPath, string singerDir) => new {
            source = Path.GetFileName(ustPath),
            singer = Path.GetFileName(Path.GetFullPath(singerDir).TrimEnd(Path.DirectorySeparatorChar)),
            resolution = project.resolution,
            tempos = project.tempos.Select(t => new { t.position, t.bpm }),
            timeSignatures = project.timeSignatures.Select(t => new { t.barPosition, t.beatPerBar, t.beatUnit }),
            part = new { part.position, part.Duration },
            notes = part.notes.Select((n, i) => new {
                index = i,
                n.position,
                n.duration,
                n.tone,
                n.lyric,
                pitch = n.pitch.data.Select(p => new { p.X, p.Y, shape = p.shape.ToString() }),
                vibrato = new { n.vibrato.length, n.vibrato.period, n.vibrato.depth, n.vibrato.@in, n.vibrato.@out, n.vibrato.shift, n.vibrato.drift },
            }),
        };

        static object ExportVoicebank(ClassicSinger singer, string singerDir) {
            singerDir = Path.GetFullPath(singerDir);
            string? Rel(string? path) => string.IsNullOrEmpty(path) ? path : Path.GetRelativePath(singerDir, path);
            var probes = singer.Otos.Select(o => o.Alias)
                .Concat(new[] { "R", "-", "あ", "* あ", "a", "", "no such alias" })
                .Distinct()
                .ToList();
            return new {
                singer.Id,
                singer.Name,
                singer.Author,
                singer.Voice,
                singer.Web,
                singer.Version,
                singer.OtherInfo,
                singerType = singer.SingerType.ToString(),
                textFileEncoding = singer.TextFileEncoding?.WebName,
                singer.DefaultPhonemizer,
                avatar = Rel(singer.Avatar),
                sample = Rel(singer.Sample),
                subbanks = singer.Subbanks.Select(s => new {
                    s.Color,
                    s.Prefix,
                    s.Suffix,
                    s.ToneRangesString,
                    tones = s.toneSet.ToArray(),
                }),
                otos = singer.Otos.Select(o => new {
                    o.Alias,
                    o.Phonetic,
                    o.Set,
                    file = Rel(o.File),
                    o.DisplayFile,
                    o.Offset,
                    o.Consonant,
                    o.Cutoff,
                    o.Preutter,
                    o.Overlap,
                    subbanks = o.Subbanks.Select(s => new { s.Color, s.Prefix, s.Suffix }),
                }),
                errors = singer.Errors,
                lookups = probes.SelectMany(phoneme => new[] { 48, 60, 72 }.Select(tone => new {
                    phoneme,
                    tone,
                    oto = singer.TryGetOto(phoneme, out var o1) ? o1.Alias : null,
                    mapped = singer.TryGetMappedOto(phoneme, tone, out var o2) ? o2.Alias : null,
                    mappedMainColor = singer.TryGetMappedOto(phoneme, tone, string.Empty, out var o3) ? o3.Alias : null,
                })),
            };
        }

        static object ExportUst(UProject project) {
            var track = project.tracks[0];
            var part = project.parts.OfType<UVoicePart>().First();
            return new {
                project.name,
                project.resolution,
                tempos = project.tempos.Select(t => new { t.position, t.bpm }),
                timeSignatures = project.timeSignatures.Select(t => new { t.barPosition, t.beatPerBar, t.beatUnit }),
                track = new {
                    singer = track.Singer?.Name,
                    expressions = track.TrackExpressions.Select(e => new { e.abbr, e.flag, value = e.CustomDefaultValue }),
                },
                part = new { part.name, part.position, part.Duration },
                notes = part.notes.Select((n, i) => new {
                    index = i,
                    n.position,
                    n.duration,
                    n.tone,
                    n.lyric,
                    n.tuning,
                    pitch = new {
                        n.pitch.snapFirst,
                        data = n.pitch.data.Select(p => new { p.X, p.Y, shape = p.shape.ToString() }),
                    },
                    vibrato = new { n.vibrato.length, n.vibrato.period, n.vibrato.depth, n.vibrato.@in, n.vibrato.@out, n.vibrato.shift, n.vibrato.drift, n.vibrato.volLink },
                    expressions = n.phonemeExpressions.Select(e => new { e.abbr, e.index, e.value }),
                }),
            };
        }

        static void ExportSavedUst(UProject project, UVoicePart part, string path, string tempDir) {
            var tmp = Path.Join(tempDir, "saved.ust");
            Ust.SavePart(project, part, tmp);
            var shiftJis = Encoding.GetEncoding("shift_jis");
            var lines = File.ReadAllLines(tmp, shiftJis).Select(line =>
                line.StartsWith("VoiceDir=") ? "VoiceDir=<singer>" :
                line.StartsWith("CacheDir=") ? "CacheDir=<cache>" : line);
            // StreamWriter.WriteLine writes Environment.NewLine ("\n" on macOS).
            File.WriteAllText(path, string.Join("\n", lines) + "\n", shiftJis);
        }

        static object ExportPhonemizer(UTrack track, UVoicePart part) {
            var notes = part.notes.ToList();
            int? IndexOf(UNote? n) => n == null ? null : notes.IndexOf(n);
            // The note groups UVoicePart.Validate sends to the phonemizer: each note that does not overlap and
            // is not an extender, with the extender notes that follow it.
            var groups = new List<object>();
            for (int i = 0; i < notes.Count; i++) {
                var note = notes[i];
                if (note.OverlapError || note.Extends != null) {
                    continue;
                }
                var group = new List<UNote> { note };
                var next = note.Next;
                while (next != null && next.Extends == note) {
                    group.Add(next);
                    next = next.Next;
                }
                groups.Add(new {
                    noteIndex = i,
                    notes = group.Select(n => n.ToPhonemizerNote(track, part)).Select(pn => new {
                        pn.lyric,
                        pn.phoneticHint,
                        pn.tone,
                        pn.position,
                        pn.duration,
                        phonemeAttributes = pn.phonemeAttributes.Select(a => new {
                            a.index, a.consonantStretchRatio, a.toneShift, a.alternate, a.voiceColor,
                        }),
                    }),
                });
            }
            return new {
                phonemizer = track.Phonemizer.GetType().FullName,
                voiceColorOptions = track.VoiceColorExp?.options,
                notes = notes.Select((n, i) => new {
                    index = i,
                    n.position,
                    n.duration,
                    n.PositionMs,
                    n.EndMs,
                    prev = IndexOf(n.Prev),
                    next = IndexOf(n.Next),
                    extends = IndexOf(n.Extends),
                    n.ExtendedDuration,
                    n.Error,
                    n.OverlapError,
                    pitch = n.pitch.data.Select(p => new { p.X, p.Y, shape = p.shape.ToString() }),
                    n.phonemeIndexes,
                    phonemizerExpressions = n.phonemizerExpressions.Select(e => new { e.abbr, e.index, e.value }),
                }),
                groups,
                phonemes = part.phonemes.Select(p => new {
                    noteIndex = IndexOf(p.Parent),
                    p.index,
                    p.rawPhoneme,
                    p.rawPosition,
                    error = p.ErrorException?.Message,
                }),
            };
        }

        static object ExportPhonemes(UVoicePart part) {
            var notes = part.notes.ToList();
            return part.phonemes.Select(p => new {
                noteIndex = notes.IndexOf(p.Parent),
                p.index,
                p.rawPhoneme,
                p.phoneme,
                p.position,
                p.Duration,
                p.End,
                p.preutter,
                p.overlap,
                p.autoPreutter,
                p.autoOverlap,
                oto = ExportOto(p.oto),
                error = p.Error,
            }).ToList();
        }

        static object? ExportOto(UOto? oto) => oto == null ? null : new {
            oto.Alias,
            file = Path.GetFileName(oto.File),
            oto.Offset,
            oto.Consonant,
            oto.Cutoff,
            oto.Preutter,
            oto.Overlap,
        };

        static object ExportResamplerItem(RenderPhrase phrase, ResamplerItem item) {
            // Same arguments as WorldlineRenderer.Render passes to PhraseSynthV2.AddRequest.
            double posMs = item.phone.positionMs - item.phone.leadingMs - (phrase.positionMs - phrase.leadingMs);
            double lengthMs = item.phone.envelope[4].X - item.phone.envelope[0].X;
            double fadeInMs = item.phone.envelope[1].X - item.phone.envelope[0].X;
            double fadeOutMs = item.phone.envelope[4].X - item.phone.envelope[3].X;
            return new {
                item.phone.phoneme,
                inputFile = Path.GetFileName(item.inputFile),
                resampler = item.resampler.ToString(),
                item.tone,
                flags = item.flags.Select(f => new { flag = f.Item1, value = f.Item2, abbr = f.Item3 }),
                flagsString = item.GetFlagsString(),
                item.velocity,
                item.volume,
                item.modulation,
                item.preutter,
                item.overlap,
                item.offset,
                item.durRequired,
                item.durCorrection,
                item.consonant,
                item.cutoff,
                item.skipOver,
                item.tempo,
                item.pitches,
                item.tension,
                item.breathiness,
                item.voicing,
                item.gender,
                request = new { posMs, skipMs = item.skipOver, lengthMs, fadeInMs, fadeOutMs },
            };
        }

        static readonly MethodInfo sampleCurve = typeof(WorldlineRenderer)
            .GetMethod("SampleCurve", BindingFlags.Instance | BindingFlags.NonPublic)!;

        /// <summary>
        /// The curves WorldlineRenderer.Render samples per frame and passes to PhraseSynthV2.SetCurves.
        /// </summary>
        static object ExportSynthCurves(WorldlineRenderer renderer, RenderPhrase phrase) {
            var frameMs = (double)typeof(WorldlineRenderer).GetField("frameMs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer)!;
            var layout = renderer.Layout(phrase);
            int frames = (int)Math.Ceiling(layout.estimatedLengthMs / frameMs);
            double[] Sample(float[] curve, double defaultValue, Func<double, double> convert) =>
                (double[])sampleCurve.Invoke(renderer, new object[] { phrase, curve, defaultValue, frames, convert })!;
            return new {
                frameMs,
                frames,
                f0 = Sample(phrase.pitches, 0, x => MusicMath.ToneToFreq(x * 0.01)),
                gender = Sample(phrase.gender, 0.5, x => 0.5 + 0.005 * x),
                tension = Sample(phrase.tension, 0.5, x => 0.5 + 0.005 * x),
                breathiness = Sample(phrase.breathiness, 0.5, x => 0.5 + 0.005 * x),
                voicing = Sample(phrase.voicing, 1.0, x => 0.01 * x),
            };
        }

        static object ExportPhrase(RenderPhrase phrase, int partPosition) => new {
            phrase.position,
            phrase.duration,
            phrase.end,
            phrase.leading,
            phrase.positionMs,
            phrase.durationMs,
            phrase.endMs,
            phrase.leadingMs,
            // pitches etc. are sampled every 5 ticks from pitchStart (part-relative), as in RenderPhrase
            pitchIntervalTicks = 5,
            pitchStart = phrase.position - partPosition - phrase.leading,
            notes = phrase.notes.Select(n => new {
                n.lyric,
                n.tone,
                n.position,
                n.duration,
                n.positionMs,
                n.durationMs,
            }),
            phones = phrase.phones.Select(p => new {
                p.phoneme,
                p.tone,
                p.noteIndex,
                p.position,
                p.duration,
                p.leading,
                p.positionMs,
                p.durationMs,
                p.leadingMs,
                p.preutterMs,
                p.overlapMs,
                p.durCorrectionMs,
                p.tempo,
                p.adjustedTempo,
                p.volume,
                p.velocity,
                p.modulation,
                p.direct,
                p.toneShift,
                flags = p.flags.Select(f => new { flag = f.Item1, value = f.Item2, abbr = f.Item3 }),
                envelope = p.envelope.Select(v => new[] { v.X, v.Y }),
                oto = ExportOto(p.oto),
            }),
            phrase.pitches,
            phrase.pitchesBeforeDeviation,
            phrase.dynamics,
            phrase.gender,
            phrase.breathiness,
            phrase.toneShift,
            phrase.tension,
            phrase.voicing,
        };
    }
}
