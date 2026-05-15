using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using FastClip.Controls;
using FastClip.Models;
using FastClip.Services;

namespace FastClip.Forms;

internal sealed class AdvancedPasteForm : Form
{
    private const int WidthInputX = 24;
    private const int HeightInputX = 264;
    private const int InputTopY = 58;
    private const int InputWidth = 146;
    private const int LinkButtonWidth = 30;
    private const int LinkButtonHeight = 30;
    private static readonly int[] ScalePresets = [90, 80, 70, 60, 50, 40, 30, 20, 10];
    private readonly PasteSession _session;
    private readonly TabControl _tabControl;
    private readonly TabPage _compressionTab;
    private readonly NumericUpDown _widthInput;
    private readonly NumericUpDown _heightInput;
    private readonly AspectRatioIconButton _linkButton;
    private readonly ComboBox _scalePresetComboBox;
    private readonly ComboBox _formatComboBox;
    private readonly CheckBox _compressionEnabledCheckBox;
    private readonly TrackBar _compressionQualityTrackBar;
    private readonly Label _compressionQualityValueLabel;
    private readonly TrackBar _pngOptimizationTrackBar;
    private readonly Label _pngOptimizationValueLabel;
    private readonly Label _compressionEstimateLabel;
    private readonly CheckBox _autoApplyCheckBox;
    private readonly System.Windows.Forms.Timer _compressionEstimateTimer;
    private readonly ToolTip _toolTip;
    private readonly IImageTransformPipeline _imageTransformPipeline;
    private readonly MozJpegEncoder _mozJpegEncoder;
    private readonly OxipngEncoder _oxipngEncoder;
    private bool _isUpdatingControls;
    private int _compressionEstimateVersion;

    public bool AutoApplyNextTime => _autoApplyCheckBox.Checked;

    public AdvancedPasteForm(PasteSession session)
    {
        _session = session;
        _toolTip = new ToolTip();
        _imageTransformPipeline = new ImageTransformPipeline();
        _mozJpegEncoder = new MozJpegEncoder();
        _oxipngEncoder = new OxipngEncoder();
        _compressionEstimateTimer = new System.Windows.Forms.Timer
        {
            Interval = 400
        };
        _compressionEstimateTimer.Tick += (_, _) => StartCompressionEstimate();
        FormClosed += (_, _) => _compressionEstimateTimer.Dispose();

        Text = "FastClip - Advanced Paste";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = Color.FromArgb(248, 250, 252);
        ClientSize = new Size(468, 362);

        _tabControl = new TabControl
        {
            Location = new Point(18, 18),
            Size = new Size(432, 274)
        };

        var resizeTab = new TabPage("Resize");
        resizeTab.BackColor = Color.White;
        _tabControl.TabPages.Add(resizeTab);
        _compressionTab = new TabPage("Compress");
        _compressionTab.BackColor = Color.White;
        _tabControl.TabPages.Add(_compressionTab);

        var resizeCaptionLabel = new Label
        {
            AutoSize = true,
            Location = new Point(24, 18),
            Text = "Adjust output size and file format before saving.",
            ForeColor = Color.FromArgb(91, 103, 112)
        };

        var widthLabel = new Label
        {
            AutoSize = true,
            Location = new Point(24, 42),
            Text = "Width"
        };

        _widthInput = new NumericUpDown
        {
            Location = new Point(WidthInputX, InputTopY),
            Width = InputWidth,
            Minimum = 1,
            Maximum = session.Options.Resize.OriginalWidth,
            Value = session.Options.Resize.Width
        };
        _widthInput.ValueChanged += (_, _) => OnWidthChanged();

        _linkButton = new AspectRatioIconButton
        {
            Location = new Point(GetLinkButtonX(), GetLinkButtonY(_widthInput)),
            Size = new Size(LinkButtonWidth, LinkButtonHeight)
        };
        _linkButton.Click += (_, _) => ToggleAspectRatio();
        UpdateLinkButtonState();

        var heightLabel = new Label
        {
            AutoSize = true,
            Location = new Point(264, 42),
            Text = "Height"
        };

        _heightInput = new NumericUpDown
        {
            Location = new Point(HeightInputX, InputTopY),
            Width = InputWidth,
            Minimum = 1,
            Maximum = session.Options.Resize.OriginalHeight,
            Value = session.Options.Resize.Height
        };
        _heightInput.ValueChanged += (_, _) => OnHeightChanged();

        var presetLabel = new Label
        {
            AutoSize = true,
            Location = new Point(24, 110),
            Text = "Scale Preset"
        };

        _scalePresetComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(24, 134),
            Width = 386
        };
        _scalePresetComboBox.Items.Add("Custom");
        foreach (var preset in ScalePresets)
        {
            _scalePresetComboBox.Items.Add($"{preset}%");
        }
        _scalePresetComboBox.SelectedIndexChanged += (_, _) => OnScalePresetChanged();

        var formatLabel = new Label
        {
            AutoSize = true,
            Location = new Point(24, 182),
            Text = "Format"
        };

        _formatComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(24, 208),
            Width = 386
        };
        _formatComboBox.Items.AddRange(
        [
            new FormatOption(".jpg", "JPG"),
            new FormatOption(".png", "PNG"),
            new FormatOption(".bmp", "BMP"),
            new FormatOption(".gif", "GIF"),
            new FormatOption(".tif", "TIFF")
        ]);
        _formatComboBox.SelectedIndexChanged += (_, _) => OnFormatChanged();

        resizeTab.Controls.Add(resizeCaptionLabel);
        resizeTab.Controls.Add(widthLabel);
        resizeTab.Controls.Add(_widthInput);
        resizeTab.Controls.Add(_linkButton);
        resizeTab.Controls.Add(heightLabel);
        resizeTab.Controls.Add(_heightInput);
        resizeTab.Controls.Add(presetLabel);
        resizeTab.Controls.Add(_scalePresetComboBox);
        resizeTab.Controls.Add(formatLabel);
        resizeTab.Controls.Add(_formatComboBox);

        _compressionEnabledCheckBox = new CheckBox
        {
            AutoSize = true,
            Text = "Enable compression"
        };
        _compressionEnabledCheckBox.CheckedChanged += (_, _) => OnCompressionEnabledChanged();
        _compressionQualityTrackBar = new TrackBar();
        _compressionQualityValueLabel = new Label();
        _pngOptimizationTrackBar = new TrackBar();
        _pngOptimizationValueLabel = new Label();
        _compressionEstimateLabel = new Label();

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(286, 312),
            Width = 78,
            Height = 34
        };

        _autoApplyCheckBox = new CheckBox
        {
            AutoSize = false,
            Location = new Point(20, 308),
            Size = new Size(236, 40),
            Text = "Use these settings next time",
            TextAlign = ContentAlignment.MiddleLeft
        };
        _toolTip.SetToolTip(_autoApplyCheckBox, "Apply these settings automatically next time and skip this window.");

        var saveButton = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            Location = new Point(372, 312),
            Width = 78,
            Height = 34
        };
        saveButton.Click += (_, _) => OnSave();

        AcceptButton = saveButton;
        CancelButton = cancelButton;

        Controls.Add(_tabControl);
        Controls.Add(_autoApplyCheckBox);
        Controls.Add(cancelButton);
        Controls.Add(saveButton);

        UpdatePresetSelection();
        UpdateFormatSelection();
        RebuildCompressionTab();
        ScheduleCompressionEstimate();
    }

    private void ToggleAspectRatio()
    {
        _session.Options.Resize.KeepAspectRatio = !_session.Options.Resize.KeepAspectRatio;
        UpdateLinkButtonState();
        if (_session.Options.Resize.KeepAspectRatio)
        {
            ApplyScaledDimensions((int)_widthInput.Value, resizeBasedOnWidth: true);
        }
    }

    private void UpdateLinkButtonState()
    {
        _linkButton.IsLinked = _session.Options.Resize.KeepAspectRatio;
        _linkButton.Invalidate();
        _toolTip.SetToolTip(
            _linkButton,
            _session.Options.Resize.KeepAspectRatio
                ? "Aspect ratio locked"
                : "Aspect ratio unlocked");
    }

    private void OnWidthChanged()
    {
        if (_isUpdatingControls)
        {
            return;
        }

        if (_session.Options.Resize.KeepAspectRatio)
        {
            ApplyScaledDimensions((int)_widthInput.Value, resizeBasedOnWidth: true);
            return;
        }

        UpdateWidthOnly((int)_widthInput.Value);
    }

    private void OnHeightChanged()
    {
        if (_isUpdatingControls)
        {
            return;
        }

        if (_session.Options.Resize.KeepAspectRatio)
        {
            ApplyScaledDimensions((int)_heightInput.Value, resizeBasedOnWidth: false);
            return;
        }

        UpdateHeightOnly((int)_heightInput.Value);
    }

    private void OnScalePresetChanged()
    {
        if (_isUpdatingControls)
        {
            return;
        }

        if (_scalePresetComboBox.SelectedItem is not string selection || selection == "Custom")
        {
            return;
        }

        var percent = int.Parse(selection.TrimEnd('%'));
        var width = Math.Max(1, _session.Options.Resize.OriginalWidth * percent / 100);
        ApplyScaledDimensions(width, resizeBasedOnWidth: true, percent);
    }

    private void OnFormatChanged()
    {
        if (_isUpdatingControls || _session.TargetKind != PasteTargetKind.NewFile || _formatComboBox.SelectedItem is not FormatOption option)
        {
            return;
        }

        _session.SetOutputExtension(option.Extension);
        RebuildCompressionTab();
        ScheduleCompressionEstimate();
    }

    private void OnCompressionQualityChanged()
    {
        if (_isUpdatingControls ||
            !_session.Options.Compression.Enabled ||
            !_session.Options.Compression.AvailableForCurrentTarget ||
            _session.Options.Compression.TargetFormat != CompressionTargetFormat.Jpeg)
        {
            return;
        }

        _session.Options.Compression.JpegQuality = _compressionQualityTrackBar.Value;
        _compressionQualityValueLabel.Text = _compressionQualityTrackBar.Value.ToString();
        ScheduleCompressionEstimate();
    }

    private void OnPngOptimizationLevelChanged()
    {
        if (_isUpdatingControls ||
            !_session.Options.Compression.Enabled ||
            !_session.Options.Compression.AvailableForCurrentTarget ||
            _session.Options.Compression.TargetFormat != CompressionTargetFormat.Png)
        {
            return;
        }

        _session.Options.Compression.PngOptimizationLevel = _pngOptimizationTrackBar.Value;
        _pngOptimizationValueLabel.Text = _pngOptimizationTrackBar.Value.ToString();
        ScheduleCompressionEstimate();
    }

    private void OnCompressionEnabledChanged()
    {
        if (_isUpdatingControls || !_session.Options.Compression.AvailableForCurrentTarget)
        {
            return;
        }

        _session.Options.Compression.Enabled = _compressionEnabledCheckBox.Checked;
        UpdateCompressionControlsEnabledState();
        ScheduleCompressionEstimate();
    }

    private void ApplyScaledDimensions(int primaryValue, bool resizeBasedOnWidth, int? scalePercent = null)
    {
        var original = _session.Options.Resize;
        var width = resizeBasedOnWidth ? primaryValue : Math.Max(1, (int)Math.Round(primaryValue * (original.OriginalWidth / (double)original.OriginalHeight)));
        var height = resizeBasedOnWidth ? Math.Max(1, (int)Math.Round(width * (original.OriginalHeight / (double)original.OriginalWidth))) : primaryValue;

        width = Math.Min(width, original.OriginalWidth);
        height = Math.Min(height, original.OriginalHeight);

        UpdateControls(width, height, scalePercent);
    }

    private void UpdateWidthOnly(int width)
    {
        UpdateResizeOptions(width, _session.Options.Resize.Height, null);
    }

    private void UpdateHeightOnly(int height)
    {
        UpdateResizeOptions(_session.Options.Resize.Width, height, null);
    }

    private void UpdateResizeOptions(int width, int height, int? scalePercent)
    {
        width = Math.Clamp(width, 1, _session.Options.Resize.OriginalWidth);
        height = Math.Clamp(height, 1, _session.Options.Resize.OriginalHeight);
        _session.Options.Resize.Width = width;
        _session.Options.Resize.Height = height;
        _session.Options.Resize.ScalePercent = scalePercent;
        UpdatePresetSelection();
        ScheduleCompressionEstimate();
    }

    private void UpdateControls(int width, int height, int? scalePercent)
    {
        _isUpdatingControls = true;
        try
        {
            _widthInput.Value = width;
            _heightInput.Value = height;
            UpdateResizeOptions(width, height, scalePercent);
        }
        finally
        {
            _isUpdatingControls = false;
        }
    }

    private void UpdatePresetSelection()
    {
        _isUpdatingControls = true;
        try
        {
            var percent = _session.Options.Resize.ScalePercent;
            _scalePresetComboBox.SelectedItem = percent.HasValue ? $"{percent.Value}%" : "Custom";
        }
        finally
        {
            _isUpdatingControls = false;
        }
    }

    private void UpdateFormatSelection()
    {
        _isUpdatingControls = true;
        try
        {
            var selected = _formatComboBox.Items
                .OfType<FormatOption>()
                .FirstOrDefault(item => string.Equals(item.Extension, _session.OutputExtension, StringComparison.OrdinalIgnoreCase));

            if (selected is null)
            {
                selected = new FormatOption(_session.OutputExtension, _session.OutputExtension.TrimStart('.').ToUpperInvariant());
                _formatComboBox.Items.Add(selected);
            }

            _formatComboBox.SelectedItem = selected ?? _formatComboBox.Items.OfType<FormatOption>().FirstOrDefault();
            _formatComboBox.Enabled = _session.TargetKind == PasteTargetKind.NewFile;
        }
        finally
        {
            _isUpdatingControls = false;
        }
    }

    private void RebuildCompressionTab()
    {
        _compressionTab.SuspendLayout();
        try
        {
            _compressionTab.Controls.Clear();

            if (_session.Options.Compression.AvailableForCurrentTarget)
            {
                _compressionTab.Enabled = true;

                if (_session.Options.Compression.TargetFormat == CompressionTargetFormat.Jpeg)
                {
                    var captionLabel = new Label
                    {
                        AutoSize = true,
                        Location = new Point(24, 18),
                        Text = "Control JPEG quality before saving.",
                        ForeColor = Color.FromArgb(91, 103, 112)
                    };

                    var compressionLabel = new Label
                    {
                        AutoSize = true,
                        Location = new Point(24, 78),
                        Text = "Quality"
                    };

                    _compressionEnabledCheckBox.Location = new Point(24, 44);
                    _compressionEnabledCheckBox.Checked = _session.Options.Compression.Enabled;

                    _compressionQualityTrackBar.Minimum = 0;
                    _compressionQualityTrackBar.Maximum = 100;
                    _compressionQualityTrackBar.TickFrequency = 10;
                    _compressionQualityTrackBar.SmallChange = 1;
                    _compressionQualityTrackBar.LargeChange = 5;
                    _compressionQualityTrackBar.Location = new Point(24, 104);
                    _compressionQualityTrackBar.Width = 336;
                    _compressionQualityTrackBar.ValueChanged -= CompressionQualityTrackBarValueChanged;
                    _compressionQualityTrackBar.Value = Math.Clamp(_session.Options.Compression.JpegQuality, 0, 100);
                    _compressionQualityTrackBar.ValueChanged += CompressionQualityTrackBarValueChanged;

                    _compressionQualityValueLabel.AutoSize = true;
                    _compressionQualityValueLabel.Location = new Point(370, 110);
                    _compressionQualityValueLabel.Text = _compressionQualityTrackBar.Value.ToString();

                    _compressionEstimateLabel.AutoSize = false;
                    _compressionEstimateLabel.Location = new Point(24, 176);
                    _compressionEstimateLabel.Size = new Size(386, 40);
                    _compressionEstimateLabel.Text = "Estimated output size: calculating...";
                    _compressionEstimateLabel.ForeColor = Color.FromArgb(63, 74, 84);

                    _compressionTab.Controls.Add(captionLabel);
                    _compressionTab.Controls.Add(_compressionEnabledCheckBox);
                    _compressionTab.Controls.Add(compressionLabel);
                    _compressionTab.Controls.Add(_compressionQualityTrackBar);
                    _compressionTab.Controls.Add(_compressionQualityValueLabel);
                    _compressionTab.Controls.Add(_compressionEstimateLabel);
                }
                else if (_session.Options.Compression.TargetFormat == CompressionTargetFormat.Png)
                {
                    var captionLabel = new Label
                    {
                        AutoSize = true,
                        Location = new Point(24, 18),
                        Text = "Apply lossless PNG optimization with oxipng.",
                        ForeColor = Color.FromArgb(91, 103, 112)
                    };

                    var compressionLabel = new Label
                    {
                        AutoSize = true,
                        Location = new Point(24, 78),
                        Text = "Optimization Level"
                    };

                    _compressionEnabledCheckBox.Location = new Point(24, 44);
                    _compressionEnabledCheckBox.Checked = _session.Options.Compression.Enabled;

                    _pngOptimizationTrackBar.Minimum = 0;
                    _pngOptimizationTrackBar.Maximum = 6;
                    _pngOptimizationTrackBar.TickFrequency = 1;
                    _pngOptimizationTrackBar.SmallChange = 1;
                    _pngOptimizationTrackBar.LargeChange = 1;
                    _pngOptimizationTrackBar.Location = new Point(24, 104);
                    _pngOptimizationTrackBar.Width = 336;
                    _pngOptimizationTrackBar.ValueChanged -= PngOptimizationTrackBarValueChanged;
                    _pngOptimizationTrackBar.Value = Math.Clamp(_session.Options.Compression.PngOptimizationLevel, 0, 6);
                    _pngOptimizationTrackBar.ValueChanged += PngOptimizationTrackBarValueChanged;

                    _pngOptimizationValueLabel.AutoSize = true;
                    _pngOptimizationValueLabel.Location = new Point(370, 110);
                    _pngOptimizationValueLabel.Text = _pngOptimizationTrackBar.Value.ToString();

                    var hintLabel = new Label
                    {
                        AutoSize = false,
                        Location = new Point(24, 154),
                        Size = new Size(386, 20),
                        Text = "Higher levels are slower and usually compress better.",
                        ForeColor = Color.FromArgb(91, 103, 112)
                    };

                    _compressionEstimateLabel.AutoSize = false;
                    _compressionEstimateLabel.Location = new Point(24, 190);
                    _compressionEstimateLabel.Size = new Size(386, 44);
                    _compressionEstimateLabel.Text = "Estimated output size: calculating...";
                    _compressionEstimateLabel.ForeColor = Color.FromArgb(63, 74, 84);

                    _compressionTab.Controls.Add(captionLabel);
                    _compressionTab.Controls.Add(_compressionEnabledCheckBox);
                    _compressionTab.Controls.Add(compressionLabel);
                    _compressionTab.Controls.Add(_pngOptimizationTrackBar);
                    _compressionTab.Controls.Add(_pngOptimizationValueLabel);
                    _compressionTab.Controls.Add(hintLabel);
                    _compressionTab.Controls.Add(_compressionEstimateLabel);
                }
            }
            else
            {
                _compressionTab.Enabled = false;
                _compressionTab.Controls.Add(new Label
                {
                    AutoSize = false,
                    Location = new Point(24, 28),
                    Size = new Size(386, 64),
                    Text = "Compression is currently available only for JPEG and PNG output.",
                    ForeColor = Color.FromArgb(91, 103, 112)
                });
            }

            UpdateCompressionControlsEnabledState();
        }
        finally
        {
            _compressionTab.ResumeLayout();
        }
    }

    private void CompressionQualityTrackBarValueChanged(object? sender, EventArgs e)
    {
        OnCompressionQualityChanged();
    }

    private void PngOptimizationTrackBarValueChanged(object? sender, EventArgs e)
    {
        OnPngOptimizationLevelChanged();
    }

    private void OnSave()
    {
        UpdateResizeOptions((int)_widthInput.Value, (int)_heightInput.Value, _session.Options.Resize.ScalePercent);
        if (_session.Options.Compression.AvailableForCurrentTarget)
        {
            if (_session.Options.Compression.TargetFormat == CompressionTargetFormat.Jpeg)
            {
                _session.Options.Compression.Enabled = _compressionEnabledCheckBox.Checked;
                _session.Options.Compression.JpegQuality = _compressionQualityTrackBar.Value;
                _compressionQualityValueLabel.Text = _compressionQualityTrackBar.Value.ToString();
            }
            else if (_session.Options.Compression.TargetFormat == CompressionTargetFormat.Png)
            {
                _session.Options.Compression.Enabled = _compressionEnabledCheckBox.Checked;
                _session.Options.Compression.PngOptimizationLevel = _pngOptimizationTrackBar.Value;
                _pngOptimizationValueLabel.Text = _pngOptimizationTrackBar.Value.ToString();
            }
        }
    }

    private void ScheduleCompressionEstimate()
    {
        if (!_session.Options.Compression.AvailableForCurrentTarget)
        {
            return;
        }

        _compressionEstimateTimer.Stop();
        if (!_session.Options.Compression.Enabled)
        {
            _compressionEstimateLabel.Text = "Compression disabled.";
            return;
        }

        _compressionEstimateLabel.Text = "Estimated output size: calculating...";
        _compressionEstimateTimer.Start();
    }

    private void StartCompressionEstimate()
    {
        _compressionEstimateTimer.Stop();
        if (!_session.Options.Compression.AvailableForCurrentTarget || !_session.Options.Compression.Enabled || IsDisposed)
        {
            return;
        }

        var version = Interlocked.Increment(ref _compressionEstimateVersion);
        var resizeWidth = _session.Options.Resize.Width;
        var resizeHeight = _session.Options.Resize.Height;
        var quality = _session.Options.Compression.JpegQuality;
        var pngOptimizationLevel = _session.Options.Compression.PngOptimizationLevel;
        var compressionTargetFormat = _session.Options.Compression.TargetFormat;

        _ = Task.Run(() =>
        {
            using var imageClone = new Bitmap(_session.SourceImage);
            using var transformedImage = _imageTransformPipeline.Apply(
                imageClone,
                new PasteOptions
                {
                    OutputExtension = _session.OutputExtension,
                    Resize = new ResizeOptions
                    {
                        OriginalWidth = _session.Options.Resize.OriginalWidth,
                        OriginalHeight = _session.Options.Resize.OriginalHeight,
                        Width = resizeWidth,
                        Height = resizeHeight,
                        KeepAspectRatio = _session.Options.Resize.KeepAspectRatio,
                        ScalePercent = _session.Options.Resize.ScalePercent
                    },
                    Compression = new CompressionOptions
                    {
                        Enabled = true,
                        AvailableForCurrentTarget = true,
                        TargetFormat = compressionTargetFormat,
                        JpegQuality = quality,
                        PngOptimizationLevel = pngOptimizationLevel
                    }
                });

            return compressionTargetFormat switch
            {
                CompressionTargetFormat.Jpeg => _mozJpegEncoder.EstimateSize(transformedImage, quality),
                CompressionTargetFormat.Png => _oxipngEncoder.EstimateSize(transformedImage, pngOptimizationLevel),
                _ => CompressionEstimateResult.Unavailable()
            };
        }).ContinueWith(task =>
        {
            if (IsDisposed || version != _compressionEstimateVersion)
            {
                return;
            }

            if (task.IsFaulted || task.IsCanceled)
            {
                if (IsHandleCreated)
                {
                    BeginInvoke(new Action(() => _compressionEstimateLabel.Text = "Estimated output size: unavailable"));
                }
                return;
            }

            var estimate = task.Result;
            if (!IsHandleCreated)
            {
                return;
            }

            BeginInvoke(new Action(() =>
            {
                if (IsDisposed || version != _compressionEstimateVersion)
                {
                    return;
                }

                _compressionEstimateLabel.Text = estimate.DisplayText;
            }));
        }, TaskScheduler.Default);
    }

    private void UpdateCompressionControlsEnabledState()
    {
        var enabled = _session.Options.Compression.AvailableForCurrentTarget && _session.Options.Compression.Enabled;
        _compressionQualityTrackBar.Enabled = enabled && _session.Options.Compression.TargetFormat == CompressionTargetFormat.Jpeg;
        _compressionQualityValueLabel.Enabled = _compressionQualityTrackBar.Enabled;
        _pngOptimizationTrackBar.Enabled = enabled && _session.Options.Compression.TargetFormat == CompressionTargetFormat.Png;
        _pngOptimizationValueLabel.Enabled = _pngOptimizationTrackBar.Enabled;
        _compressionEstimateLabel.Enabled = enabled;
    }

    private static int GetLinkButtonX()
    {
        var widthRight = WidthInputX + InputWidth;
        var gapCenter = widthRight + ((HeightInputX - widthRight) / 2);
        return gapCenter - (LinkButtonWidth / 2);
    }

    private static int GetLinkButtonY(Control alignedControl)
    {
        return alignedControl.Top + ((alignedControl.Height - LinkButtonHeight) / 2);
    }
}
