using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace RewireGuard.Services;

/// <summary>
/// Loads the ONNX-exported ViT classifier and runs inference on a captured frame or a
/// sub-rectangle of one. Normalization matches HF's ViTImageProcessor default
/// (mean/std = 0.5 per channel) -- double check against the actual preprocessor_config.json
/// for the model you export.
///
/// Not thread-safe: the input tensor and scratch bitmaps are reused between calls to keep the
/// poll loop off the allocator. Callers must serialize access (the app runs one scan at a time).
/// </summary>
public sealed class NsfwClassifier : IDisposable
{
    private const float Mean = 0.5f;
    private const float Std = 0.5f;

    private readonly InferenceSession _session;
    private readonly int _inputSize;
    private readonly int _nsfwIndex;
    private readonly string _inputName;
    private readonly string _outputName;

    // Reused across calls -- see the thread-safety note above.
    private readonly DenseTensor<float> _tensor;
    private readonly List<NamedOnnxValue> _inputs;
    private readonly Bitmap _scratch;
    private readonly Graphics _scratchGraphics;
    private readonly ImageAttributes _drawAttributes;
    private Bitmap? _halfway;
    private Graphics? _halfwayGraphics;

    public string ExecutionProvider { get; }

    public NsfwClassifier(string modelPath, int inputSize, int nsfwIndex)
    {
        _inputSize = inputSize;
        _nsfwIndex = nsfwIndex;

        (_session, ExecutionProvider) = CreateSession(modelPath);

        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();

        var outputRank = _session.OutputMetadata[_outputName].Dimensions;
        if (outputRank.Length == 2 && outputRank[1] > 0 && nsfwIndex >= outputRank[1])
        {
            throw new InvalidOperationException(
                $"NsfwLabelIndex={nsfwIndex} is out of range for a model with {outputRank[1]} classes. " +
                "Check id2label in the exported model's config.json.");
        }

        _tensor = new DenseTensor<float>(new[] { 1, 3, _inputSize, _inputSize });
        _inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, _tensor) };

        _scratch = new Bitmap(_inputSize, _inputSize, PixelFormat.Format24bppRgb);
        _scratchGraphics = Graphics.FromImage(_scratch);
        _scratchGraphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        _scratchGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        _scratchGraphics.SmoothingMode = SmoothingMode.None;
        _scratchGraphics.CompositingQuality = CompositingQuality.HighQuality;

        // Without TileFlipXY, GDI+ samples past the edge of the source rect and leaves a bright
        // halo around every tile -- visible enough to shift the classifier's output.
        _drawAttributes = new ImageAttributes();
        _drawAttributes.SetWrapMode(WrapMode.TileFlipXY);

        WarmUp();
    }

    private static (InferenceSession Session, string Provider) CreateSession(string modelPath)
    {
        // Ask the runtime what it actually has rather than attempting each provider blind. The
        // old code tried OpenVINO, then CUDA:1, then CUDA:0, then CPU -- and since no CUDA
        // package is referenced, both CUDA attempts were guaranteed to fail only after trying
        // to load a 344 MB model, adding seconds to every cold start.
        string[] available;
        try
        {
            available = OrtEnv.Instance().GetAvailableProviders();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not enumerate ONNX execution providers ({ex.Message}); assuming CPU only.");
            available = new[] { "CPUExecutionProvider" };
        }

        Log.Info($"ONNX execution providers available: {string.Join(", ", available)}");

        foreach (var candidate in PreferredProviders(available))
        {
            var sw = Stopwatch.StartNew();
            SessionOptions? opts = null;
            try
            {
                opts = new SessionOptions();
                candidate.Configure(opts);
                var session = new InferenceSession(modelPath, opts);
                Log.Info($"Loaded model on {candidate.Name} in {sw.ElapsedMilliseconds} ms.");
                return (session, candidate.Name);
            }
            catch (Exception ex)
            {
                opts?.Dispose();   // SessionOptions is unmanaged; the old code leaked one per attempt
                Log.Warn($"Execution provider {candidate.Name} unavailable: {ex.Message}");
            }
        }

        // Plain CPU, no options object to leak.
        var cpuSw = Stopwatch.StartNew();
        var cpuSession = new InferenceSession(modelPath);
        Log.Info($"Loaded model on CPU in {cpuSw.ElapsedMilliseconds} ms.");
        return (cpuSession, "CPU");
    }

    private readonly record struct ProviderCandidate(string Name, Action<SessionOptions> Configure);

    private static IEnumerable<ProviderCandidate> PreferredProviders(string[] available)
    {
        bool Has(string name) => available.Any(p => p.Contains(name, StringComparison.OrdinalIgnoreCase));

        if (Has("OpenVINO"))
        {
            yield return new ProviderCandidate("OpenVINO-NPU", o =>
                o.AppendExecutionProvider("OpenVINO", new Dictionary<string, string> { ["device_type"] = "NPU" }));
            yield return new ProviderCandidate("OpenVINO-GPU", o =>
                o.AppendExecutionProvider("OpenVINO", new Dictionary<string, string> { ["device_type"] = "GPU" }));
        }

        if (Has("DML"))
            yield return new ProviderCandidate("DirectML", o => o.AppendExecutionProvider("DML"));

        if (Has("CUDA"))
            yield return new ProviderCandidate("CUDA", o => o.AppendExecutionProvider_CUDA(0));
    }

    /// <summary>
    /// First inference pays for graph optimization and kernel JIT -- on CPU that can be several
    /// seconds. Doing it here means the cost lands at startup instead of on the poll that matters.
    /// </summary>
    private void WarmUp()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            using var blank = new Bitmap(_inputSize, _inputSize, PixelFormat.Format24bppRgb);
            ClassifyNsfwProbability(blank);
            Log.Info($"Warm-up inference took {sw.ElapsedMilliseconds} ms on {ExecutionProvider}.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Warm-up inference failed: {ex.Message}");
        }
    }

    /// <summary>Returns the model's probability that the frame is NSFW, in [0, 1].</summary>
    public double ClassifyNsfwProbability(Bitmap frame) =>
        ClassifyNsfwProbability(frame, new Rectangle(0, 0, frame.Width, frame.Height));

    /// <summary>
    /// Returns the model's probability that <paramref name="sourceRect"/> of the frame is NSFW.
    /// Taking a rectangle rather than a pre-cropped bitmap removes one full allocation and copy
    /// per tile per poll.
    /// </summary>
    public double ClassifyNsfwProbability(Bitmap frame, Rectangle sourceRect)
    {
        var clipped = Rectangle.Intersect(sourceRect, new Rectangle(0, 0, frame.Width, frame.Height));
        if (clipped.Width <= 0 || clipped.Height <= 0) return 0.0;

        ResizeInto(frame, clipped);
        FillTensorFromScratch();

        using var results = _session.Run(_inputs);
        var logits = results.First(r => r.Name == _outputName).AsEnumerable<float>().ToArray();

        if (_nsfwIndex >= logits.Length)
        {
            Log.Error($"Model returned {logits.Length} logits but NsfwLabelIndex is {_nsfwIndex}.");
            return 0.0;
        }

        return Softmax(logits)[_nsfwIndex];
    }

    /// <summary>
    /// Draws the source rect into the square scratch bitmap.
    ///
    /// GDI+ bicubic does not area-average, so collapsing a 2560x1440 desktop straight to 384x384
    /// (a ~7x reduction) aliases hard -- thin features drop out entirely and the classifier sees
    /// a different image than it would from a properly downsampled one. Halving repeatedly until
    /// we are within 2x of the target keeps the detail that matters, and costs a rounding error
    /// next to a ViT forward pass.
    /// </summary>
    private void ResizeInto(Bitmap frame, Rectangle sourceRect)
    {
        var source = frame;
        var rect = sourceRect;

        int reduction = Math.Max(sourceRect.Width / _inputSize, sourceRect.Height / _inputSize);
        if (reduction >= 2)
        {
            int halfW = Math.Max(_inputSize, sourceRect.Width / 2);
            int halfH = Math.Max(_inputSize, sourceRect.Height / 2);

            if (_halfway == null || _halfway.Width < halfW || _halfway.Height < halfH)
            {
                _halfwayGraphics?.Dispose();
                _halfway?.Dispose();
                _halfway = new Bitmap(halfW, halfH, PixelFormat.Format24bppRgb);
                _halfwayGraphics = Graphics.FromImage(_halfway);
                _halfwayGraphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                _halfwayGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            }

            _halfwayGraphics!.DrawImage(
                frame,
                new Rectangle(0, 0, halfW, halfH),
                rect.X, rect.Y, rect.Width, rect.Height,
                GraphicsUnit.Pixel,
                _drawAttributes);

            source = _halfway;
            rect = new Rectangle(0, 0, halfW, halfH);
        }

        _scratchGraphics.DrawImage(
            source,
            new Rectangle(0, 0, _inputSize, _inputSize),
            rect.X, rect.Y, rect.Width, rect.Height,
            GraphicsUnit.Pixel,
            _drawAttributes);
    }

    private void FillTensorFromScratch()
    {
        var rect = new Rectangle(0, 0, _inputSize, _inputSize);
        var data = _scratch.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                byte* basePtr = (byte*)data.Scan0;
                int stride = data.Stride;

                // Write straight into the tensor's backing span; the indexer does bounds maths
                // per element, which showed up in profiles at 442k pixels per inference.
                var buffer = _tensor.Buffer.Span;
                int plane = _inputSize * _inputSize;

                for (int y = 0; y < _inputSize; y++)
                {
                    byte* row = basePtr + y * stride;
                    int rowOffset = y * _inputSize;

                    for (int x = 0; x < _inputSize; x++)
                    {
                        // Format24bppRgb byte order is BGR
                        byte b = row[x * 3 + 0];
                        byte g = row[x * 3 + 1];
                        byte r = row[x * 3 + 2];

                        int i = rowOffset + x;
                        buffer[i] = (r / 255f - Mean) / Std;
                        buffer[plane + i] = (g / 255f - Mean) / Std;
                        buffer[2 * plane + i] = (b / 255f - Mean) / Std;
                    }
                }
            }
        }
        finally
        {
            _scratch.UnlockBits(data);
        }
    }

    private static float[] Softmax(float[] logits)
    {
        float max = logits.Max();
        var exps = logits.Select(l => MathF.Exp(l - max)).ToArray();
        float sum = exps.Sum();
        return exps.Select(e => e / sum).ToArray();
    }

    public void Dispose()
    {
        _session.Dispose();
        _scratchGraphics.Dispose();
        _scratch.Dispose();
        _drawAttributes.Dispose();
        _halfwayGraphics?.Dispose();
        _halfway?.Dispose();
    }
}
