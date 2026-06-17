using NumberPlate.Services;
namespace NumberPlate.Pages;

public partial class Text : ContentPage
{
	public Text()
	{
		InitializeComponent();
	}

    private async void OnCapturePhotoClicked(object sender, EventArgs e)
    {
        try
        {
            var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
            if (status != PermissionStatus.Granted)
                status = await Permissions.RequestAsync<Permissions.Camera>();

            if (status != PermissionStatus.Granted)
            {
                await DisplayAlert("Permission Denied", "Camera permission is required", "OK");
                return;
            }

            var photo = await MediaPicker.Default.CapturePhotoAsync();
            if (photo != null)
                await ProcessImageAsync(photo);
        }
        catch (FeatureNotSupportedException)
        {
            await DisplayAlert("Not Supported", "Camera is not supported on this device", "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Failed to capture photo: {ex.Message}", "OK");
        }
    }

    // ------------------------------------------------------------------
    // Gallery pick
    // ------------------------------------------------------------------
    private async void OnPickPhotoClicked(object sender, EventArgs e)
    {
        try
        {
            if (DeviceInfo.Platform == DevicePlatform.Android)
            {
                var status = await Permissions.CheckStatusAsync<Permissions.StorageRead>();
                if (status != PermissionStatus.Granted)
                    status = await Permissions.RequestAsync<Permissions.StorageRead>();

                if (status != PermissionStatus.Granted)
                {
                    await DisplayAlert("Permission Denied", "Storage permission is required", "OK");
                    return;
                }
            }

            var photo = await MediaPicker.Default.PickPhotoAsync();
            if (photo != null)
                await ProcessImageAsync(photo);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Failed to pick photo: {ex.Message}", "OK");
        }
    }

    // ------------------------------------------------------------------
    // Core processing — delegates to VnprService
    // ------------------------------------------------------------------
    private async Task ProcessImageAsync(FileResult photo)
    {
        SetLoadingState(true);
        ClearResults();

        try
        {
            // Show preview
            using var previewStream = await photo.OpenReadAsync();
            var ms = new MemoryStream();
            await previewStream.CopyToAsync(ms);
            ms.Position = 0;
            PreviewImage.Source = ImageSource.FromStream(() =>
            {
                ms.Position = 0;
                return ms;
            });

            // Read bytes
            byte[] imageBytes;
            using var imgStream = await photo.OpenReadAsync();
            using var memStream = new MemoryStream();
            await imgStream.CopyToAsync(memStream);
            imageBytes = memStream.ToArray();

            // Run VNPR pipeline
            var result = await VnprService.ProcessAsync(imageBytes);

            if (result.Success)
            {
                ShowSuccess(result);
            }
            else
            {
                ShowError(result.Error ?? "UNKNOWN_ERROR");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VNPR Page] Error: {ex}");
            ShowError("PROCESSING_FAILED");
        }
        finally
        {
            SetLoadingState(false);
        }
    }

    // ------------------------------------------------------------------
    // UI helpers
    // ------------------------------------------------------------------
    private void ShowSuccess(VnprResult result)
    {
        VehicleNumberLabel.Text = result.VehicleNumber;
        VehicleNumberLabel.IsVisible = true;

        PlateColorBadge.Text = result.PlateColor switch
        {
            "YELLOW" => "🟡 Commercial",
            "GREEN" => "🟢 Electric",
            "BLACK" => "⬛ Rental",
            _ => "⬜ Private"
        };
        PlateColorBadge.IsVisible = true;

        ConfidenceLabel.Text = $"Confidence: {result.Confidence:F1}%";
        ConfidenceLabel.TextColor = result.Confidence >= 95 ? Colors.Green : Colors.Orange;
        ConfidenceLabel.IsVisible = true;

        CaptureTimeLabel.Text = $"Captured: {result.CaptureTime}";
        CaptureTimeLabel.IsVisible = true;

        if (!string.IsNullOrEmpty(result.CroppedImagePath))
        {
            SavedPathLabel.Text = $"Saved: {Path.GetFileName(result.CroppedImagePath)}";
            SavedPathLabel.IsVisible = true;
        }

        StatusLabel.Text = "✅ Plate Recognized";
        StatusLabel.TextColor = Colors.Green;
    }

    private void ShowError(string errorCode)
    {
        StatusLabel.Text = errorCode switch
        {
            "NO_PLATE_FOUND" => "❌ No number plate detected",
            "MULTIPLE_PLATES_DETECTED" => "⚠️ Multiple plates in frame — focus on one",
            "LOW_CONFIDENCE" => "⚠️ Low confidence — try better lighting",
            "IMAGE_BLURRY" => "⚠️ Image is blurry — hold steady",
            _ => $"❌ {errorCode}"
        };
        StatusLabel.TextColor = Colors.Red;
    }

    private void ClearResults()
    {
        VehicleNumberLabel.IsVisible = false;
        PlateColorBadge.IsVisible = false;
        ConfidenceLabel.IsVisible = false;
        CaptureTimeLabel.IsVisible = false;
        SavedPathLabel.IsVisible = false;
        StatusLabel.Text = string.Empty;
    }

    private void SetLoadingState(bool isLoading)
    {
        LoadingIndicator.IsRunning = isLoading;
        LoadingIndicator.IsVisible = isLoading;
        CaptureButton.IsEnabled = !isLoading;
        PickButton.IsEnabled = !isLoading;
    }
}