using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Plugin.Maui.OCR;
using SkiaSharp;
namespace NumberPlate.Services
{
    public class VnprResult
    {
        public bool Success { get; set; }
        public string? VehicleNumber { get; set; }
        public string? PlateColor { get; set; }
        public double Confidence { get; set; }
        public string? CaptureTime { get; set; }
        public string? Error { get; set; }
        public string? CroppedImagePath { get; set; }
        public bool RequiresManualVerification { get; set; }  // per spec §12
    }

    public static class VnprService
    {
        // ---------------------------------------------------------------
        // All supported Indian plate formats
        // ---------------------------------------------------------------
        private static readonly System.Text.RegularExpressions.Regex[] PlatePatterns = new[]
        {
        // Standard: AP39CD1234
        new System.Text.RegularExpressions.Regex(
            @"^[A-Z]{2}[0-9]{1,2}[A-Z]{1,3}[0-9]{3,4}$",
            System.Text.RegularExpressions.RegexOptions.Compiled),

        // Ends with letter (vanity/fancy): AP39CD123A
        new System.Text.RegularExpressions.Regex(
            @"^[A-Z]{2}[0-9]{1,2}[A-Z]{1,3}[0-9]{2,3}[A-Z]$",
            System.Text.RegularExpressions.RegexOptions.Compiled),

        // Digit-first (old/diplomatic/special): 22BH6517A or 22BH6517
        new System.Text.RegularExpressions.Regex(
            @"^[0-9]{1,2}[A-Z]{2}[0-9]{3,4}[A-Z]?$",
            System.Text.RegularExpressions.RegexOptions.Compiled),

        // Short / no series letters: AP396517
        new System.Text.RegularExpressions.Regex(
            @"^[A-Z]{2}[0-9]{1,2}[0-9]{4}$",
            System.Text.RegularExpressions.RegexOptions.Compiled),
    };

        private static bool IsValidPlate(string text) =>
            PlatePatterns.Any(p => p.IsMatch(text));

        // Known non-plate tokens to skip during matching
        private static readonly HashSet<string> _skipTokens = new()
    {
        "IND", "BH", "IN", "INDIA", "HSP", "HSRP"
    };

        private static readonly Dictionary<string, DateTime> _recentReads = new();
        private static readonly int _duplicateSuppressSeconds = 30;

        // ---------------------------------------------------------------
        // Entry point
        // ---------------------------------------------------------------
        public static async Task<VnprResult> ProcessAsync(byte[] imageBytes)
        {
            try
            {
                // Step 1: Run OCR on full image
                var ocrService = OcrPlugin.Default;
                var ocrResult = await ocrService.RecognizeTextAsync(imageBytes, tryHard: true);

                System.Diagnostics.Debug.WriteLine($"[VNPR] Raw OCR text: '{ocrResult?.AllText}'");

                if (ocrResult == null || !ocrResult.Success || string.IsNullOrWhiteSpace(ocrResult.AllText))
                    return Fail("NO_PLATE_FOUND");

                // Step 2: Extract valid Indian plate numbers from OCR text
                var matches = FindAllPlateMatches(ocrResult.AllText);

                System.Diagnostics.Debug.WriteLine($"[VNPR] Plate matches found: {matches.Count}");
                foreach (var m in matches)
                    System.Diagnostics.Debug.WriteLine($"[VNPR]   Match: {m}");

                if (matches.Count == 0)
                    return Fail("NO_PLATE_FOUND");

                if (matches.Count > 1)
                    return Fail("MULTIPLE_PLATES_DETECTED");

                var vehicleNumber = matches[0];

                // Step 3: Confidence
                double confidence = EstimateConfidence(vehicleNumber, ocrResult.Elements);
                System.Diagnostics.Debug.WriteLine($"[VNPR] Confidence: {confidence:F1}%");

                // Per spec §12: < 80% = reject, 80-90% = pass with manual verification warning
                if (confidence < 80.0)
                    return Fail("LOW_CONFIDENCE");

                bool needsVerification = confidence < 90.0;

                // Step 4: Duplicate suppression
                if (IsDuplicate(vehicleNumber))
                {
                    System.Diagnostics.Debug.WriteLine($"[VNPR] Duplicate suppressed: {vehicleNumber}");
                    return Fail("NO_PLATE_FOUND");
                }
                MarkRead(vehicleNumber);

                // Step 5: Crop plate region
                var croppedBytes = TryCropPlateRegion(imageBytes, vehicleNumber, ocrResult.Elements)
                                   ?? imageBytes;

                // Step 6: Sharpness check
                if (!IsSharp(croppedBytes))
                    return Fail("IMAGE_BLURRY");

                // Step 7: Plate color
                var plateColor = DetectPlateColor(croppedBytes);

                // Step 8: Save
                var savedPath = await SaveCroppedPlateAsync(croppedBytes, vehicleNumber, confidence);

                return new VnprResult
                {
                    Success = true,
                    VehicleNumber = vehicleNumber,
                    PlateColor = plateColor,
                    Confidence = Math.Round(confidence, 1),
                    CaptureTime = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                    CroppedImagePath = savedPath,
                    RequiresManualVerification = needsVerification
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VNPR] Unhandled error: {ex}");
                return Fail("NO_PLATE_FOUND");
            }
        }

        // ---------------------------------------------------------------
        // Find all valid Indian plate numbers in raw OCR text
        // ---------------------------------------------------------------
        private static List<string> FindAllPlateMatches(string rawText)
        {
            var found = new List<string>();

            var tokens = rawText
                .ToUpperInvariant()
                .Split(new[] { '\n', '\r', ' ', '\t', '-', '.', ',' },
                       StringSplitOptions.RemoveEmptyEntries)
                .Select(t => System.Text.RegularExpressions.Regex.Replace(t, @"[^A-Z0-9]", ""))
                .Where(t => t.Length >= 2)
                .ToList();

            // 1-token
            foreach (var token in tokens)
            {
                var corrected = CorrectOcrMisreads(token);
                if (IsValidPlate(corrected) && !found.Contains(corrected))
                    found.Add(corrected);
            }

            // Substring scan inside long tokens
            foreach (var token in tokens.Where(t => t.Length > 7))
            {
                for (int start = 0; start <= token.Length - 7; start++)
                {
                    for (int len = 7; len <= Math.Min(10, token.Length - start); len++)
                    {
                        var sub = CorrectOcrMisreads(token.Substring(start, len));
                        if (IsValidPlate(sub) && !found.Contains(sub))
                            found.Add(sub);
                    }
                }
            }

            var filteredTokens = tokens
                .Where(t => !_skipTokens.Contains(t))
                .ToList();

            // 2-token
            for (int i = 0; i < filteredTokens.Count - 1; i++)
            {
                var t0variants = new List<string> { filteredTokens[i] };
                if (filteredTokens[i].Length > 2)
                    t0variants.Add(filteredTokens[i].Substring(1));

                foreach (var t0 in t0variants)
                {
                    var combined = CorrectOcrMisreads(t0 + filteredTokens[i + 1]);
                    if (IsValidPlate(combined) && !found.Contains(combined))
                        found.Add(combined);
                }
            }

            // 3-token
            for (int i = 0; i < filteredTokens.Count - 2; i++)
            {
                var t0variants = new List<string> { filteredTokens[i] };
                if (filteredTokens[i].Length > 2)
                    t0variants.Add(filteredTokens[i].Substring(1));

                foreach (var t0 in t0variants)
                {
                    var combined = CorrectOcrMisreads(t0 + filteredTokens[i + 1] + filteredTokens[i + 2]);
                    if (IsValidPlate(combined) && !found.Contains(combined))
                        found.Add(combined);
                }
            }

            // 4-token
            for (int i = 0; i < filteredTokens.Count - 3; i++)
            {
                var t0 = filteredTokens[i];
                bool looksLikePlateStart =
                    (t0.Length >= 2 && char.IsLetter(t0[0]) && char.IsLetter(t0[1])) ||
                    (t0.Length >= 2 && char.IsDigit(t0[0]) && char.IsDigit(t0[1]));

                if (!looksLikePlateStart) continue;

                var combined = CorrectOcrMisreads(
                    t0 + filteredTokens[i + 1] + filteredTokens[i + 2] + filteredTokens[i + 3]);
                if (IsValidPlate(combined) && !found.Contains(combined))
                    found.Add(combined);
            }

            // ---------------------------------------------------------------
            // Dedup: if multiple matches found, keep only the longest one
            // that contains all shorter ones as substrings.
            // e.g. ["99GX0777", "9GX0777", "HR99GX0777"] → ["HR99GX0777"]
            // ---------------------------------------------------------------
            return DeduplicateMatches(found);
        }

        private static List<string> DeduplicateMatches(List<string> matches)
        {
            if (matches.Count <= 1) return matches;

            // Sort longest first
            var sorted = matches.OrderByDescending(m => m.Length).ToList();

            var kept = new List<string>();

            foreach (var candidate in sorted)
            {
                // Check if this candidate is a substring of any already-kept longer match
                bool isSubOfKept = kept.Any(k => k.Contains(candidate));

                // Check if this candidate contains any already-kept match as substring
                bool containsKept = kept.Any(k => candidate.Contains(k));

                if (!isSubOfKept)
                {
                    // If it contains a shorter kept match, replace the shorter one
                    if (containsKept)
                    {
                        kept.RemoveAll(k => candidate.Contains(k));
                    }
                    kept.Add(candidate);
                }
            }

            System.Diagnostics.Debug.WriteLine($"[VNPR] After dedup: {string.Join(", ", kept)}");
            return kept;
        }

        // ---------------------------------------------------------------
        // OCR misread correction
        // ---------------------------------------------------------------
        private static string CorrectOcrMisreads(string text)
        {
            if (text.Length < 6) return text;
            var c = text.ToCharArray();

            bool digitFirst = char.IsDigit(c[0]) || char.IsDigit(c[1]);

            if (!digitFirst)
            {
                // Positions 0-1: must be letters (state code)
                for (int i = 0; i < 2 && i < c.Length; i++)
                {
                    if (c[i] == '0') c[i] = 'O';
                    if (c[i] == '1') c[i] = 'I';
                    if (c[i] == '5') c[i] = 'S';
                    if (c[i] == '8') c[i] = 'B';
                }

                // Positions 2-3: must be digits (district)
                for (int i = 2; i < 4 && i < c.Length; i++)
                {
                    if (c[i] == 'O' || c[i] == 'Q') c[i] = '0';
                    if (c[i] == 'I' || c[i] == 'L') c[i] = '1';
                    if (c[i] == 'S') c[i] = '5';
                    if (c[i] == 'B') c[i] = '8';
                    if (c[i] == 'Z') c[i] = '2';
                    if (c[i] == 'G') c[i] = '6';
                }
            }
            else
            {
                // Positions 0-1: must be digits
                for (int i = 0; i < 2 && i < c.Length; i++)
                {
                    if (c[i] == 'O' || c[i] == 'Q') c[i] = '0';
                    if (c[i] == 'I' || c[i] == 'L') c[i] = '1';
                    if (c[i] == 'S') c[i] = '5';
                    if (c[i] == 'B') c[i] = '8';
                    if (c[i] == 'Z') c[i] = '2';
                }
            }

            int seriesStart = digitFirst ? 2 : 4;

            // Handle optional trailing vanity letter
            bool hasTrailingLetter = char.IsLetter(c[c.Length - 1]);
            int trailingLetterPos = hasTrailingLetter ? c.Length - 1 : c.Length;

            // Walk backwards to find trailing digit block start
            int digitBlockStart = trailingLetterPos;
            for (int i = trailingLetterPos - 1; i >= seriesStart; i--)
            {
                char ch = c[i];
                bool isDigitLike = char.IsDigit(ch) ||
                                   ch == 'O' || ch == 'Q' ||
                                   ch == 'I' || ch == 'L' ||
                                   ch == 'S' || ch == 'B' ||
                                   ch == 'Z' || ch == 'G';
                if (isDigitLike)
                    digitBlockStart = i;
                else
                    break;
            }

            // Correct trailing digit block → digits
            for (int i = digitBlockStart; i < trailingLetterPos; i++)
            {
                if (c[i] == 'O' || c[i] == 'Q') c[i] = '0';
                if (c[i] == 'I' || c[i] == 'L') c[i] = '1';
                if (c[i] == 'S') c[i] = '5';
                if (c[i] == 'B') c[i] = '8';
                if (c[i] == 'G') c[i] = '6';
                if (c[i] == 'Z') c[i] = '2';
            }

            // Correct middle series letters → letters
            for (int i = seriesStart; i < digitBlockStart && i < c.Length; i++)
            {
                if (c[i] == '0') c[i] = 'O';
                if (c[i] == '1') c[i] = 'I';
                if (c[i] == '5') c[i] = 'S';
                if (c[i] == '8') c[i] = 'B';
            }

            return new string(c);
        }

        // ---------------------------------------------------------------
        // Confidence — 85% format weight, 15% element weight
        // ---------------------------------------------------------------
        private static double EstimateConfidence(
            string vehicleNumber, IList<OcrResult.OcrElement>? elements)
        {
            double baseScore = 92.0;
            int corrections = CountCorrections(vehicleNumber);
            double correctionPenalty = corrections * 2.5;
            double formatScore = Math.Max(85.0, baseScore - correctionPenalty);

            if (elements != null && elements.Count > 0)
            {
                var relevant = elements
                    .Where(e => !string.IsNullOrWhiteSpace(e.Text))
                    .Where(e =>
                    {
                        var t = System.Text.RegularExpressions.Regex
                            .Replace(e.Text.Trim().ToUpperInvariant(), @"[^A-Z0-9]", "");
                        if (t.Length < 2) return false;
                        return vehicleNumber.Contains(t) ||
                               vehicleNumber.Contains(CorrectOcrMisreads(t)) ||
                               IsSubstringOfPlate(vehicleNumber, t);
                    })
                    .ToList();

                if (relevant.Count > 0)
                {
                    double elementAvg = relevant.Average(e => e.Confidence) * 100.0;
                    System.Diagnostics.Debug.WriteLine(
                        $"[VNPR] Element avg: {elementAvg:F1}%, Format score: {formatScore:F1}%");

                    double blended = (formatScore * 0.85) + (elementAvg * 0.15);
                    return Math.Min(99.9, blended);
                }
            }

            return formatScore;
        }

        private static bool IsSubstringOfPlate(string vehicleNumber, string token)
        {
            if (token.Length > vehicleNumber.Length) return false;
            for (int i = 0; i <= vehicleNumber.Length - token.Length; i++)
            {
                var segment = vehicleNumber.Substring(i, token.Length);
                if (segment == token || segment == CorrectOcrMisreads(token))
                    return true;
            }
            return false;
        }

        private static int CountCorrections(string vehicleNumber)
        {
            int corrections = 0;
            var c = vehicleNumber.ToCharArray();

            bool digitFirst = c.Length > 0 && char.IsDigit(c[0]);

            if (!digitFirst)
            {
                for (int i = 0; i < 2 && i < c.Length; i++)
                    if (!char.IsLetter(c[i])) corrections++;
                for (int i = 2; i < 4 && i < c.Length; i++)
                    if (!char.IsDigit(c[i])) corrections++;
            }
            else
            {
                for (int i = 0; i < 2 && i < c.Length; i++)
                    if (!char.IsDigit(c[i])) corrections++;
            }

            int checkEnd = c.Length - 1;
            if (char.IsLetter(c[checkEnd])) checkEnd--;
            for (int i = Math.Max(0, checkEnd - 3); i <= checkEnd; i++)
                if (!char.IsDigit(c[i])) corrections++;

            return corrections;
        }

        // ---------------------------------------------------------------
        // Crop plate region — element bbox first, then color fallback
        // ---------------------------------------------------------------
        private static byte[]? TryCropPlateRegion(
            byte[] imageBytes, string vehicleNumber, IList<OcrResult.OcrElement>? elements)
        {
            try
            {
                if (elements != null && elements.Count > 0)
                {
                    var plateElements = elements
                        .Where(e => !string.IsNullOrWhiteSpace(e.Text) &&
                                    vehicleNumber.Contains(
                                        CorrectOcrMisreads(
                                            e.Text.Trim().ToUpperInvariant())))
                        .ToList();

                    if (plateElements.Count > 0)
                    {
                        float minX = plateElements.Min(e => e.X);
                        float minY = plateElements.Min(e => e.Y);
                        float maxX = plateElements.Max(e => e.X + e.Width);
                        float maxY = plateElements.Max(e => e.Y + e.Height);

                        using var original = SKBitmap.Decode(imageBytes);
                        if (original != null)
                        {
                            float x, y, w, h;
                            if (maxX <= 1.0f && maxY <= 1.0f)
                            {
                                x = minX * original.Width;
                                y = minY * original.Height;
                                w = (maxX - minX) * original.Width;
                                h = (maxY - minY) * original.Height;
                            }
                            else
                            {
                                x = minX; y = minY;
                                w = maxX - minX; h = maxY - minY;
                            }

                            float padX = w * 0.2f, padY = h * 0.5f;
                            var rect = new SKRectI(
                                (int)Math.Max(0, x - padX),
                                (int)Math.Max(0, y - padY),
                                (int)Math.Min(original.Width, x + w + padX),
                                (int)Math.Min(original.Height, y + h + padY)
                            );

                            if (rect.Width >= 10 && rect.Height >= 5)
                            {
                                var cropped = CropBitmap(original, rect);
                                if (cropped != null)
                                {
                                    System.Diagnostics.Debug.WriteLine(
                                        $"[VNPR] Cropped via elements: {rect.Width}x{rect.Height}");
                                    return cropped;
                                }
                            }
                        }
                    }
                }

                return CropByColorRegion(imageBytes);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VNPR] Crop failed: {ex.Message}");
                return null;
            }
        }

        private static byte[]? CropBitmap(SKBitmap original, SKRectI rect)
        {
            try
            {
                using var cropped = new SKBitmap(rect.Width, rect.Height);
                using var canvas = new SKCanvas(cropped);
                canvas.DrawBitmap(original,
                    new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
                    new SKRect(0, 0, rect.Width, rect.Height));
                using var img = SKImage.FromBitmap(cropped);
                using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                return data.ToArray();
            }
            catch { return null; }
        }

        private static byte[]? CropByColorRegion(byte[] imageBytes)
        {
            try
            {
                using var bitmap = SKBitmap.Decode(imageBytes);
                if (bitmap == null) return null;

                int w = bitmap.Width, h = bitmap.Height;
                var rowScores = new double[h];

                for (int y = 0; y < h; y++)
                {
                    double score = 0;
                    int sampleCount = 0;
                    for (int x = w / 4; x < 3 * w / 4; x++)
                    {
                        var p = bitmap.GetPixel(x, y);
                        score += p.Red + p.Green - 2.0 * p.Blue;
                        sampleCount++;
                    }
                    rowScores[y] = sampleCount > 0 ? score / sampleCount : 0;
                }

                double maxScore = rowScores.Max();
                if (maxScore <= 0) return null;

                double threshold = maxScore * 0.6;
                int bandStart = -1, bandEnd = -1;

                for (int y = 0; y < h; y++)
                {
                    if (rowScores[y] >= threshold)
                    {
                        if (bandStart == -1) bandStart = y;
                        bandEnd = y;
                    }
                }

                if (bandStart == -1 || bandEnd - bandStart < 5) return null;

                int padY = (bandEnd - bandStart) / 3;
                var rect = new SKRectI(0, Math.Max(0, bandStart - padY),
                                       w, Math.Min(h, bandEnd + padY));

                System.Diagnostics.Debug.WriteLine(
                    $"[VNPR] Color-region crop: rows {rect.Top}-{rect.Bottom}");

                return CropBitmap(bitmap, rect);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VNPR] CropByColorRegion failed: {ex.Message}");
                return null;
            }
        }

        // ---------------------------------------------------------------
        // Sharpness — Laplacian variance
        // ---------------------------------------------------------------
        private static bool IsSharp(byte[] imageBytes, double threshold = 30.0)
        {
            try
            {
                using var bitmap = SKBitmap.Decode(imageBytes);
                if (bitmap == null) return true;

                using var gray = ToGrayscale(bitmap);
                int w = gray.Width, h = gray.Height;
                if (w < 3 || h < 3) return true;

                double sum = 0, sumSq = 0;
                int count = 0;

                for (int row = 1; row < h - 1; row++)
                {
                    for (int col = 1; col < w - 1; col++)
                    {
                        int lap =
                            -gray.GetPixel(col - 1, row).Red
                            - gray.GetPixel(col + 1, row).Red
                            - gray.GetPixel(col, row - 1).Red
                            - gray.GetPixel(col, row + 1).Red
                            + 4 * gray.GetPixel(col, row).Red;
                        sum += lap;
                        sumSq += (double)lap * lap;
                        count++;
                    }
                }

                double mean = sum / count;
                double variance = (sumSq / count) - (mean * mean);
                System.Diagnostics.Debug.WriteLine($"[VNPR] Sharpness variance: {variance:F1}");
                return variance >= threshold;
            }
            catch { return true; }
        }

        // ---------------------------------------------------------------
        // Plate color detection
        // ---------------------------------------------------------------
        private static string DetectPlateColor(byte[] croppedBytes)
        {
            try
            {
                using var bitmap = SKBitmap.Decode(croppedBytes);
                if (bitmap == null) return "WHITE";

                int w = bitmap.Width, h = bitmap.Height;
                long r = 0, g = 0, b = 0;
                int count = 0;

                int startY = h / 3, endY = 2 * h / 3;
                int startX = w / 4, endX = 3 * w / 4;

                for (int y = startY; y < endY; y++)
                {
                    for (int x = startX; x < endX; x++)
                    {
                        var pixel = bitmap.GetPixel(x, y);
                        r += pixel.Red;
                        g += pixel.Green;
                        b += pixel.Blue;
                        count++;
                    }
                }

                if (count == 0) return "WHITE";

                double avgR = (double)r / count;
                double avgG = (double)g / count;
                double avgB = (double)b / count;

                System.Diagnostics.Debug.WriteLine(
                    $"[VNPR] Color sample R:{avgR:F0} G:{avgG:F0} B:{avgB:F0}");

                double warmth = avgR + avgG - 2 * avgB;

                if (warmth > 80 && avgR > 120 && avgG > 100 && avgR > avgB + 40) return "YELLOW";
                if (avgG > avgR + 25 && avgG > avgB + 25 && avgG > 90) return "GREEN";
                if (avgR < 90 && avgG < 90 && avgB < 90) return "BLACK";
                return "WHITE";
            }
            catch { return "WHITE"; }
        }

        // ---------------------------------------------------------------
        // Duplicate suppression
        // ---------------------------------------------------------------
        private static bool IsDuplicate(string vehicleNumber)
        {
            if (_recentReads.TryGetValue(vehicleNumber, out var lastSeen))
                return (DateTime.Now - lastSeen).TotalSeconds < _duplicateSuppressSeconds;
            return false;
        }

        private static void MarkRead(string vehicleNumber)
        {
            _recentReads[vehicleNumber] = DateTime.Now;
            var stale = _recentReads
                .Where(kv => (DateTime.Now - kv.Value).TotalSeconds > _duplicateSuppressSeconds * 2)
                .Select(kv => kv.Key).ToList();
            foreach (var k in stale) _recentReads.Remove(k);
        }

        // ---------------------------------------------------------------
        // Save cropped plate + JSON sidecar
        // ---------------------------------------------------------------
        private static async Task<string?> SaveCroppedPlateAsync(
          byte[] croppedBytes, string vehicleNumber, double confidence)
        {
            try
            {
                var folder = Path.Combine(FileSystem.AppDataDirectory, "VnprCaptures");
                Directory.CreateDirectory(folder);

                var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");

                // Confidence first 2 digits: e.g. 87.8% → "87"
                var confPrefix = ((int)Math.Floor(confidence)).ToString("D2");

                // e.g. 87_22BH6517A_20260605_102640.png
                var imgPath = Path.Combine(folder, $"{confPrefix}_{vehicleNumber}_{ts}.png");
                var jsonPath = Path.Combine(folder, $"{confPrefix}_{vehicleNumber}_{ts}.json");
                await File.WriteAllBytesAsync(imgPath, croppedBytes);

                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    vehicleNumber,
                    confidence = Math.Round(confidence, 1),
                    captureTime = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                    imagePath = imgPath
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

                await File.WriteAllTextAsync(jsonPath, json);
                System.Diagnostics.Debug.WriteLine($"[VNPR] Saved to: {imgPath}");
                return imgPath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VNPR] Save error: {ex.Message}");
                return null;
            }
        }
        // ---------------------------------------------------------------
        // Grayscale conversion helper
        // ---------------------------------------------------------------
        private static SKBitmap ToGrayscale(SKBitmap src)
        {
            var bmp = new SKBitmap(src.Width, src.Height, SKColorType.Gray8, SKAlphaType.Opaque);
            using var canvas = new SKCanvas(bmp);
            using var paint = new SKPaint
            {
                ColorFilter = SKColorFilter.CreateColorMatrix(new float[]
                {
                0.2126f, 0.7152f, 0.0722f, 0, 0,
                0.2126f, 0.7152f, 0.0722f, 0, 0,
                0.2126f, 0.7152f, 0.0722f, 0, 0,
                0,       0,       0,       1, 0
                })
            };
            canvas.DrawBitmap(src, 0, 0, paint);
            return bmp;
        }

        // ---------------------------------------------------------------
        // Error result factory
        // ---------------------------------------------------------------
        private static VnprResult Fail(string code) => new()
        {
            Success = false,
            Error = code,
            CaptureTime = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")
        };
    }
}
