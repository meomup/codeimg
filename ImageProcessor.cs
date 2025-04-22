using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Auto_TCD
{
    public class ImageProcessor
    {
        private readonly string _inputFolder;
        private readonly string _resizeFolder;
        private readonly string _watermarkedFolder;
        private readonly Image _watermarkImage;
        private readonly int _maxWidth;
        private readonly int _maxHeight;
        private readonly float _opacity;
        private readonly int _maxConcurrentTasks;
        private readonly CancellationTokenSource _cancellationTokenSource;

        /// <summary>
        /// Khởi tạo đối tượng xử lý ảnh
        /// </summary>
        /// <param name="inputFolder">Thư mục chứa ảnh đầu vào</param>
        /// <param name="watermarkImagePath">Đường dẫn đến ảnh watermark</param>
        /// <param name="maxWidth">Chiều rộng tối đa sau khi resize</param>
        /// <param name="maxHeight">Chiều cao tối đa sau khi resize</param>
        /// <param name="opacity">Độ trong suốt của watermark (0-1)</param>
        /// <param name="maxConcurrentTasks">Số lượng tác vụ xử lý đồng thời</param>
        public ImageProcessor(
            string inputFolder,
            string watermarkImagePath,
            int maxWidth = 1280,
            int maxHeight = 720,
            float opacity = 0.5f,
            int maxConcurrentTasks = 5)
        {
            if (!Directory.Exists(inputFolder))
                throw new DirectoryNotFoundException($"Thư mục {inputFolder} không tồn tại.");

            if (!File.Exists(watermarkImagePath))
                throw new FileNotFoundException($"File watermark {watermarkImagePath} không tồn tại.");

            _inputFolder = inputFolder;
            _resizeFolder = Path.Combine(inputFolder, "resize");
            _watermarkedFolder = Path.Combine(inputFolder, "watermarked");
            _watermarkImage = Image.FromFile(watermarkImagePath);
            _maxWidth = maxWidth;
            _maxHeight = maxHeight;
            _opacity = opacity;
            _maxConcurrentTasks = maxConcurrentTasks;
            _cancellationTokenSource = new CancellationTokenSource();

            // Tạo thư mục đầu ra nếu chưa tồn tại
            if (!Directory.Exists(_resizeFolder))
                Directory.CreateDirectory(_resizeFolder);

            if (!Directory.Exists(_watermarkedFolder))
                Directory.CreateDirectory(_watermarkedFolder);
        }

        /// <summary>
        /// Bắt đầu xử lý tất cả ảnh trong thư mục đầu vào
        /// </summary>
        /// <returns>Task hoàn thành khi tất cả ảnh được xử lý</returns>
        public async Task ProcessAllImagesAsync(IProgress<ProgressInfo> progress = null)
        {
            var supportedExtensions = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif" };
            var imageFiles = Directory.GetFiles(_inputFolder, "*.*", SearchOption.TopDirectoryOnly)
                            .Where(file => supportedExtensions.Contains(Path.GetExtension(file).ToLower()))
                            .ToList();

            int totalFiles = imageFiles.Count;
            int processedFiles = 0;

            var semaphore = new SemaphoreSlim(_maxConcurrentTasks);
            var tasks = new List<Task>();

            foreach (var imageFile in imageFiles)
            {
                await semaphore.WaitAsync();

                Task task = Task.Run(() =>
                {
                    try
                    {
                        if (_cancellationTokenSource.Token.IsCancellationRequested)
                            return;

                        string fileName = Path.GetFileName(imageFile);
                        string resizedFilePath = Path.Combine(_resizeFolder, fileName);
                        string watermarkedFilePath = Path.Combine(_watermarkedFolder, fileName);

                        // Xử lý ảnh
                        using (var originalImage = Image.FromFile(imageFile))
                        {
                            // Resize ảnh
                            using (var resizedImage = ResizeImage(originalImage, _maxWidth, _maxHeight))
                            {
                                // Lưu ảnh đã resize
                                SaveImage(resizedImage, resizedFilePath);

                                // Thêm watermark và lưu
                                using (var watermarkedImage = AddWatermark(resizedImage, _watermarkImage, _opacity))
                                {
                                    SaveImage(watermarkedImage, watermarkedFilePath);
                                }
                            }
                        }

                        Interlocked.Increment(ref processedFiles);
                        progress?.Report(new ProgressInfo
                        {
                            TotalFiles = totalFiles,
                            ProcessedFiles = processedFiles,
                            CurrentFile = fileName
                        });
                    }
                    catch (Exception ex)
                    {
                        // Xử lý ngoại lệ ở đây - có thể ghi log
                        Console.WriteLine($"Lỗi xử lý file {imageFile}: {ex.Message}");
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, _cancellationTokenSource.Token);

                tasks.Add(task);
            }

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Hủy quá trình xử lý
        /// </summary>
        public void Cancel()
        {
            _cancellationTokenSource.Cancel();
        }

        /// <summary>
        /// Giải phóng tài nguyên
        /// </summary>
        public void Dispose()
        {
            _watermarkImage?.Dispose();
            _cancellationTokenSource?.Dispose();
        }

        #region Image Processing Methods

        /// <summary>
        /// Resize ảnh và giữ nguyên tỷ lệ
        /// </summary>
        private static Bitmap ResizeImage(Image image, int maxWidth, int maxHeight)
        {
            if (image.Width <= maxWidth && image.Height <= maxHeight)
            {
                // Nếu ảnh đã nhỏ hơn kích thước yêu cầu, giữ nguyên kích thước
                var result = new Bitmap(image.Width, image.Height);
                using (var graphics = Graphics.FromImage(result))
                {
                    graphics.CompositingQuality = CompositingQuality.HighQuality;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.SmoothingMode = SmoothingMode.HighQuality;
                    graphics.DrawImage(image, 0, 0, image.Width, image.Height);
                }
                return result;
            }

            // Tính toán tỷ lệ để giữ nguyên tỷ lệ ảnh
            double ratioX = (double)maxWidth / image.Width;
            double ratioY = (double)maxHeight / image.Height;
            double ratio = Math.Min(ratioX, ratioY);

            int newWidth = (int)(image.Width * ratio);
            int newHeight = (int)(image.Height * ratio);

            // Tạo bitmap mới với kích thước mới
            var newImage = new Bitmap(newWidth, newHeight);

            // Tạo đối tượng Graphics từ bitmap mới
            using (var graphics = Graphics.FromImage(newImage))
            {
                // Cấu hình chất lượng
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.HighQuality;

                // Vẽ ảnh gốc lên bitmap mới với kích thước mới
                graphics.DrawImage(image, 0, 0, newWidth, newHeight);
            }

            return newImage;
        }

        /// <summary>
        /// Thêm watermark vào ảnh tại vị trí thích hợp
        /// </summary>
        private static Bitmap AddWatermark(Image sourceImage, Image watermarkImage, float opacity)
        {
            // Tạo bản sao của ảnh gốc
            Bitmap resultImage = new Bitmap(sourceImage.Width, sourceImage.Height);

            // Đặt độ phân giải của ảnh kết quả giống với ảnh gốc
            resultImage.SetResolution(sourceImage.HorizontalResolution, sourceImage.VerticalResolution);

            using (Graphics graphics = Graphics.FromImage(resultImage))
            {
                // Vẽ ảnh gốc
                graphics.DrawImage(sourceImage, 0, 0, sourceImage.Width, sourceImage.Height);

                // Resize watermark nếu quá lớn
                Image watermarkToUse = watermarkImage;
                bool needToDispose = false;

                if (watermarkImage.Width > sourceImage.Width * 0.5 || watermarkImage.Height > sourceImage.Height * 0.5)
                {
                    int maxWatermarkWidth = (int)(sourceImage.Width * 0.3);
                    int maxWatermarkHeight = (int)(sourceImage.Height * 0.3);
                    watermarkToUse = ResizeImage(watermarkImage, maxWatermarkWidth, maxWatermarkHeight);
                    needToDispose = true;
                }

                try
                {
                    // Tìm vị trí tối ưu để đặt watermark
                    Point watermarkPosition = FindOptimalWatermarkPosition((Bitmap)sourceImage, watermarkToUse.Size);

                    // Tạo color matrix với độ mờ được chỉ định
                    ColorMatrix colorMatrix = new ColorMatrix
                    {
                        Matrix33 = opacity
                    };

                    // Tạo attributes cho việc vẽ ảnh
                    ImageAttributes imageAttributes = new ImageAttributes();
                    imageAttributes.SetColorMatrix(colorMatrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);

                    // Vẽ watermark lên ảnh gốc với độ mờ được chỉ định
                    graphics.DrawImage(
                        watermarkToUse,
                        new Rectangle(watermarkPosition.X, watermarkPosition.Y, watermarkToUse.Width, watermarkToUse.Height),
                        0, 0, watermarkToUse.Width, watermarkToUse.Height,
                        GraphicsUnit.Pixel,
                        imageAttributes);
                }
                finally
                {
                    if (needToDispose)
                    {
                        watermarkToUse.Dispose();
                    }
                }
            }

            return resultImage;
        }

        /// <summary>
        /// Tìm vị trí phù hợp để đặt watermark
        /// </summary>
        private static Point FindOptimalWatermarkPosition(Bitmap image, Size watermarkSize)
        {
            // Mảng lưu 9 vị trí ưu tiên (chia ảnh thành lưới 3x3)
            Point[] preferredPositions = new Point[9];

            int imageWidth = image.Width;
            int imageHeight = image.Height;

            // Tọa độ X cho cột trái, giữa, phải
            int leftX = imageWidth / 10;
            int centerX = (imageWidth - watermarkSize.Width) / 2;
            int rightX = imageWidth - watermarkSize.Width - leftX;

            // Tọa độ Y cho hàng trên, giữa, dưới
            int topY = imageHeight / 10;
            int centerY = (imageHeight - watermarkSize.Height) / 2;
            int bottomY = imageHeight - watermarkSize.Height - topY;

            // Đặt 9 vị trí ưu tiên
            preferredPositions[0] = new Point(leftX, topY);         // Trên trái
            preferredPositions[1] = new Point(centerX, topY);       // Trên giữa
            preferredPositions[2] = new Point(rightX, topY);        // Trên phải
            preferredPositions[3] = new Point(leftX, centerY);      // Giữa trái
            preferredPositions[4] = new Point(centerX, centerY);    // Chính giữa
            preferredPositions[5] = new Point(rightX, centerY);     // Giữa phải
            preferredPositions[6] = new Point(leftX, bottomY);      // Dưới trái
            preferredPositions[7] = new Point(centerX, bottomY);    // Dưới giữa
            preferredPositions[8] = new Point(rightX, bottomY);     // Dưới phải

            // Dùng bản đồ độ sáng để tìm vùng tối ưu
            double[,] brightnessMap = CreateBrightnessMapFast(image);

            double bestScore = double.MaxValue;
            Point bestPosition = preferredPositions[8]; // Mặc định góc dưới phải

            // Kiểm tra 9 vị trí ưu tiên
            foreach (var position in preferredPositions)
            {
                // Đảm bảo watermark nằm trong ảnh
                if (position.X < 0 || position.Y < 0 ||
                    position.X + watermarkSize.Width > imageWidth ||
                    position.Y + watermarkSize.Height > imageHeight)
                    continue;

                double score = CalculateRegionBrightnessVariance(
                    brightnessMap,
                    position.X, position.Y,
                    watermarkSize.Width, watermarkSize.Height);

                if (score < bestScore)
                {
                    bestScore = score;
                    bestPosition = position;
                }
            }

            return bestPosition;
        }

        /// <summary>
        /// Tạo bản đồ độ sáng hiệu suất cao
        /// </summary>
        private static double[,] CreateBrightnessMapFast(Bitmap image)
        {
            int width = image.Width;
            int height = image.Height;
            double[,] brightnessMap = new double[width, height];

            BitmapData bmpData = null;

            try
            {
                // Khóa bộ nhớ của bitmap để truy cập trực tiếp dữ liệu
                bmpData = image.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format24bppRgb);

                int bytesPerPixel = 3; // Format24bppRgb
                int stride = bmpData.Stride;
                int bufferSize = stride * height;

                byte[] rgbValues = new byte[bufferSize];

                // Sao chép dữ liệu bitmap vào mảng
                Marshal.Copy(bmpData.Scan0, rgbValues, 0, bufferSize);

                // Tính toán giá trị độ sáng
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        // Định vị byte đầu tiên của pixel
                        int pos = y * stride + x * bytesPerPixel;

                        // Lấy giá trị RGB (trong Format24bppRgb, thứ tự là BGR)
                        byte blue = rgbValues[pos];
                        byte green = rgbValues[pos + 1];
                        byte red = rgbValues[pos + 2];

                        // Tính độ sáng
                        brightnessMap[x, y] = 0.299 * red + 0.587 * green + 0.114 * blue;
                    }
                }
            }
            finally
            {
                // Giải phóng bộ nhớ đã khóa
                if (bmpData != null)
                    image.UnlockBits(bmpData);
            }

            return brightnessMap;
        }

        /// <summary>
        /// Tính toán độ biến thiên về độ sáng trong một vùng của ảnh
        /// </summary>
        private static double CalculateRegionBrightnessVariance(double[,] brightnessMap, int startX, int startY, int width, int height)
        {
            double sum = 0;
            double squareSum = 0;
            int count = 0;

            // Lấy mẫu độ sáng của các điểm ảnh trong vùng (mỗi 4 pixel lấy 1)
            for (int y = startY; y < startY + height; y += 2)
            {
                for (int x = startX; x < startX + width; x += 2)
                {
                    double brightness = brightnessMap[x, y];
                    sum += brightness;
                    squareSum += brightness * brightness;
                    count++;
                }
            }

            if (count == 0)
                return double.MaxValue;

            // Tính phương sai (variance)
            double mean = sum / count;
            double variance = (squareSum / count) - (mean * mean);

            return variance;
        }

        /// <summary>
        /// Lưu ảnh với định dạng phù hợp
        /// </summary>
        private static void SaveImage(Image image, string filePath)
        {
            string extension = Path.GetExtension(filePath).ToLower();
            ImageFormat format = ImageFormat.Jpeg; // Mặc định

            switch (extension)
            {
                case ".png":
                    format = ImageFormat.Png;
                    break;
                case ".bmp":
                    format = ImageFormat.Bmp;
                    break;
                case ".gif":
                    format = ImageFormat.Gif;
                    break;
            }

            image.Save(filePath, format);
        }

        #endregion
    }

    /// <summary>
    /// Thông tin tiến trình xử lý
    /// </summary>
    public class ProgressInfo
    {
        public int TotalFiles { get; set; }
        public int ProcessedFiles { get; set; }
        public string CurrentFile { get; set; }

        public double PercentComplete => (double)ProcessedFiles / TotalFiles * 100;
    }
}
