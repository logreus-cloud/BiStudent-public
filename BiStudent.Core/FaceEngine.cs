using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace BiStudent;

public sealed record FaceMatch(OpenCvSharp.Rect Bounds, int? StudentId, double Similarity, int? CandidateId = null, double Margin = 0);

public sealed record SampleQuality(bool Ok, string? Problem, int FaceSize, double Sharpness, double Brightness);
public sealed record CapturedSample(byte[] Image, float[] Embedding);

public sealed class FaceEngine : IDisposable
{
    private static readonly (double X, double Y)[] AlignedLandmarks =
    [
        (38.2946, 51.6963), (73.5318, 51.5014), (56.0252, 71.7366),
        (41.5493, 92.3655), (70.7299, 92.2041)
    ];

    private readonly string detectionModel;
    private readonly Net recognitionNet;
    private FaceDetectorYN? detector;
    private OpenCvSharp.Size detectorSize;
    private readonly Dictionary<OpenCvSharp.Size, FaceDetectorYN> scaledDetectors = new();
    private readonly List<(int StudentId, float[] Vector)> enrolled = new();

    // OpenCV Zoo uses 0.363 for cosine matching. A slightly higher threshold
    // and a margin between different students reduce accidental attendance marks.
    public const double DefaultMatchThreshold = 0.43;
    public const double DefaultMinimumMargin = 0.06;
    public const int MinimumSampleFace = 100;
    public const double MinimumSharpness = 40;
    public const double MinimumBrightness = 50;
    public const double MaximumBrightness = 210;
    public double MatchThreshold { get; set; } = DefaultMatchThreshold;
    public double MinimumMargin { get; set; } = DefaultMinimumMargin;

    public FaceEngine()
    {
        var models = Path.Combine(AppContext.BaseDirectory, "Models");
        var detectionSource = Path.Combine(models, "face_detection_yunet_2023mar.onnx");
        var recognitionSource = Path.Combine(models, "face_recognition_sface_2021dec.onnx");
        if (!File.Exists(detectionSource)) throw new FileNotFoundException("Не найдена модель YuNet", detectionSource);
        if (!File.Exists(recognitionSource)) throw new FileNotFoundException("Не найдена модель SFace", recognitionSource);
        detectionModel = OpenCvSafePath(detectionSource);
        var recognitionModel = OpenCvSafePath(recognitionSource);
        recognitionNet = CvDnn.ReadNetFromOnnx(recognitionModel) ?? throw new InvalidOperationException("Не удалось загрузить модель SFace.");
    }

    // Native OpenCV on Windows opens files through narrow (ANSI) paths, so a model
    // under e.g. "OneDrive\Рабочий стол" fails with "Can't read ONNX file".
    // Such models are copied once into the first folder whose path is plain ASCII.
    private static string OpenCvSafePath(string source)
    {
        if (IsAscii(source)) return source;
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BiStudent", "Models"),
            Path.Combine(Path.GetTempPath(), "BiStudent", "Models"),
            Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) is { Length: > 0 } root ? root : @"C:\", "BiStudentModels")
        ];
        foreach (var directory in candidates)
        {
            if (!IsAscii(directory) || !Path.IsPathRooted(directory)) continue;
            try
            {
                Directory.CreateDirectory(directory);
                var target = Path.Combine(directory, Path.GetFileName(source));
                if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(source).Length)
                    File.Copy(source, target, true);
                return target;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        throw new InvalidOperationException(
            $"Путь к модели содержит не латинские символы, и её не удалось скопировать в папку с латинским путём. Перенесите сервер, например, в C:\\BiStudent. Путь: {source}");
    }

    private static bool IsAscii(string value) => value.All(char.IsAscii);

    public void Train(IEnumerable<(int StudentId, byte[] Image)> samples)
    {
        enrolled.Clear();
        foreach (var sample in samples)
        {
            using var aligned = Cv2.ImDecode(sample.Image, ImreadModes.Color);
            if (aligned.Empty() || aligned.Width != 112 || aligned.Height != 112) continue;
            enrolled.Add((sample.StudentId, Feature(aligned)));
        }
    }

    public IReadOnlyList<FaceMatch> Scan(Mat frame)
    {
        using var faces = FindFaces(frame);
        var matches = new List<FaceMatch>();
        var rows = faces.Rows;
        for (var row = 0; row < rows; row++)
        {
            var bounds = Bounds(faces, row, frame.Width, frame.Height);
            if (bounds.Width < 65 || bounds.Height < 65) continue;
            int? id = null;
            int? candidateId = null;
            double best = 0;
            double margin = 0;
            if (enrolled.Count > 0)
            {
                using var aligned = Align(frame, faces, row);
                var vector = Feature(aligned);
                var scores = new Dictionary<int, double>();
                foreach (var reference in enrolled)
                {
                    var score = Dot(vector, reference.Vector);
                    if (!scores.TryGetValue(reference.StudentId, out var previous) || score > previous)
                        scores[reference.StudentId] = score;
                }
                var ranked = scores.OrderByDescending(pair => pair.Value).Take(2).ToArray();
                if (ranked.Length > 0)
                {
                    best = ranked[0].Value;
                    candidateId = ranked[0].Key;
                    margin = ranked.Length == 1 ? best : best - ranked[1].Value;
                    if (best >= MatchThreshold && margin >= MinimumMargin)
                        id = candidateId;
                }
            }
            matches.Add(new FaceMatch(bounds, id, best, candidateId, margin));
        }
        return matches;
    }

    public IReadOnlyList<OpenCvSharp.Rect> Detect(Mat frame)
    {
        using var faces = FindFaces(frame);
        var result = new List<OpenCvSharp.Rect>();
        var rows = faces.Rows;
        for (var row = 0; row < rows; row++)
        {
            var bounds = Bounds(faces, row, frame.Width, frame.Height);
            if (bounds.Width >= 80 && bounds.Height >= 80) result.Add(bounds);
        }
        return result;
    }

    public byte[] CaptureSample(Mat frame, int faceIndex = 0, bool requireSingle = false)
    {
        using var faces = FindFaces(frame);
        if (faces.Rows <= faceIndex || (requireSingle && faces.Rows != 1))
            throw new InvalidOperationException("Для образца нужно одно лицо в кадре.");
        using var aligned = Align(frame, faces, faceIndex);
        Cv2.ImEncode(".png", aligned, out var bytes);
        return bytes;
    }

    public SampleQuality AssessFrame(Mat frame)
    {
        using var faces = FindFaces(frame);
        if (faces.Rows == 0)
            return new SampleQuality(false, "В кадре нет лица", 0, 0, 0);
        if (faces.Rows != 1)
            return new SampleQuality(false, "В кадре несколько лиц — оставьте одного человека", 0, 0, 0);

        var bounds = Bounds(faces, 0, frame.Width, frame.Height);
        var faceSize = Math.Min(bounds.Width, bounds.Height);
        if (faceSize < MinimumSampleFace)
            return new SampleQuality(false, "Лицо слишком далеко — подойдите ближе к камере", faceSize, 0, 0);

        using var aligned = Align(frame, faces, 0);
        using var gray = new Mat();
        Cv2.CvtColor(aligned, gray, ColorConversionCodes.BGR2GRAY);
        using var laplacian = new Mat();
        Cv2.Laplacian(gray, laplacian, MatType.CV_64F);
        Cv2.MeanStdDev(laplacian, out _, out var deviation);
        var sharpness = deviation.Val0 * deviation.Val0;
        var brightness = Cv2.Mean(gray).Val0;
        string? problem = null;
        if (sharpness < MinimumSharpness)
            problem = "Кадр размыт — не двигайтесь";
        else if (brightness < MinimumBrightness)
            problem = "Слишком темно — добавьте света";
        else if (brightness > MaximumBrightness)
            problem = "Лицо засвечено — уберите яркий свет за спиной";
        return new SampleQuality(problem is null, problem, faceSize, sharpness, brightness);
    }

    public CapturedSample CaptureChecked(Mat frame)
    {
        var quality = AssessFrame(frame);
        if (!quality.Ok) throw new InvalidOperationException(quality.Problem);
        var image = CaptureSample(frame, requireSingle: true);
        return new CapturedSample(image, Embedding(image));
    }

    public float[] Embedding(byte[] alignedImage)
    {
        using var aligned = Cv2.ImDecode(alignedImage, ImreadModes.Color);
        if (aligned.Empty() || aligned.Width != 112 || aligned.Height != 112)
            throw new InvalidOperationException("Образец лица должен иметь размер 112×112.");
        return Feature(aligned);
    }

    public static double Similarity(float[] a, float[] b) => Dot(a, b);

    public (int StudentId, double Similarity)? MostSimilar(float[] vector, int excludeStudentId)
    {
        (int StudentId, double Similarity)? best = null;
        foreach (var sample in enrolled)
        {
            if (sample.StudentId == excludeStudentId) continue;
            var similarity = Similarity(vector, sample.Vector);
            if (best is null || similarity > best.Value.Similarity)
                best = (sample.StudentId, similarity);
        }
        return best;
    }

    private Mat FindFaces(Mat frame)
    {
        var size = new OpenCvSharp.Size(frame.Width, frame.Height);
        if (detector is null || detectorSize != size)
        {
            detector?.Dispose();
            detector = FaceDetectorYN.Create(detectionModel, "", size, 0.85f);
            detectorSize = size;
        }
        var faces = new Mat();
        try
        {
            detector.Detect(frame, faces);
            if (faces.Rows > 0 || Math.Max(frame.Width, frame.Height) <= 480) return faces;
            var longest = Math.Max(frame.Width, frame.Height);
            foreach (var longSide in new[] { 640, 480, 320 })
            {
                if (longSide >= longest) continue;
                var scale = (double)longSide / longest;
                var reducedSize = new OpenCvSharp.Size(
                    Math.Max(1, (int)Math.Round(frame.Width * scale)),
                    Math.Max(1, (int)Math.Round(frame.Height * scale)));
                if (!scaledDetectors.TryGetValue(reducedSize, out var scaledDetector))
                {
                    if (scaledDetectors.Count == 8)
                    {
                        foreach (var cached in scaledDetectors.Values)
                            cached.Dispose();
                        scaledDetectors.Clear();
                    }
                    scaledDetector = FaceDetectorYN.Create(detectionModel, "", reducedSize, 0.85f);
                    scaledDetectors.Add(reducedSize, scaledDetector);
                }
                using var reduced = new Mat();
                Cv2.Resize(frame, reduced, reducedSize, 0, 0, InterpolationFlags.Area);
                using var found = new Mat();
                scaledDetector.Detect(reduced, found);
                var foundRows = found.Rows;
                if (foundRows == 0) continue;
                for (var row = 0; row < foundRows; row++)
                    for (var col = 0; col < 14; col++)
                        found.Set(row, col, found.At<float>(row, col) *
                            (col % 2 == 0 ? (float)frame.Width / reduced.Width : (float)frame.Height / reduced.Height));
                faces.Dispose();
                return found.Clone();
            }
            return faces;
        }
        catch
        {
            faces.Dispose();
            throw;
        }
    }

    private static OpenCvSharp.Rect Bounds(Mat faces, int row, int width, int height)
    {
        var x = Math.Clamp((int)faces.At<float>(row, 0), 0, width);
        var y = Math.Clamp((int)faces.At<float>(row, 1), 0, height);
        var w = Math.Clamp((int)faces.At<float>(row, 2), 0, width - x);
        var h = Math.Clamp((int)faces.At<float>(row, 3), 0, height - y);
        return new OpenCvSharp.Rect(x, y, w, h);
    }

    private static Mat Align(Mat frame, Mat faces, int row)
    {
        var src = new (double X, double Y)[5];
        for (var i = 0; i < 5; i++)
            src[i] = (faces.At<float>(row, 4 + i * 2), faces.At<float>(row, 5 + i * 2));

        var srcX = src.Average(p => p.X);
        var srcY = src.Average(p => p.Y);
        var dstX = AlignedLandmarks.Average(p => p.X);
        var dstY = AlignedLandmarks.Average(p => p.Y);
        double numeratorA = 0, numeratorB = 0, denominator = 0;
        for (var i = 0; i < 5; i++)
        {
            var x = src[i].X - srcX;
            var y = src[i].Y - srcY;
            var u = AlignedLandmarks[i].X - dstX;
            var v = AlignedLandmarks[i].Y - dstY;
            numeratorA += x * u + y * v;
            numeratorB += x * v - y * u;
            denominator += x * x + y * y;
        }
        if (denominator < 1) throw new InvalidOperationException("Не удалось выровнять лицо.");
        var a = numeratorA / denominator;
        var b = numeratorB / denominator;
        using var transform = new Mat(2, 3, MatType.CV_64FC1);
        transform.Set(0, 0, a); transform.Set(0, 1, -b); transform.Set(0, 2, dstX - a * srcX + b * srcY);
        transform.Set(1, 0, b); transform.Set(1, 1, a); transform.Set(1, 2, dstY - b * srcX - a * srcY);
        var aligned = new Mat();
        Cv2.WarpAffine(frame, aligned, transform, new OpenCvSharp.Size(112, 112));
        return aligned;
    }

    private float[] Feature(Mat aligned)
    {
        using var blob = CvDnn.BlobFromImage(aligned, 1, new OpenCvSharp.Size(112, 112), Scalar.All(0), true, false);
        recognitionNet.SetInput(blob);
        using var output = recognitionNet.Forward();
        output.GetArray<float>(out var vector);
        var length = Math.Sqrt(vector.Sum(value => (double)value * value));
        if (length <= 0) throw new InvalidOperationException("Модель не вернула признаки лица.");
        for (var i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / length);
        return vector;
    }

    private static double Dot(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double result = 0;
        for (var i = 0; i < a.Length; i++) result += a[i] * b[i];
        return result;
    }

    public void Dispose()
    {
        detector?.Dispose();
        foreach (var cached in scaledDetectors.Values)
            cached.Dispose();
        recognitionNet.Dispose();
    }
}
