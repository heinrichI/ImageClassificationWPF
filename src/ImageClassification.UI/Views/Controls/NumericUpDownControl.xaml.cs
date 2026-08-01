using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ImageClassification.UI.Views.Controls
{
    public partial class NumericUpDownControl : UserControl
    {
        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(nameof(Value), typeof(double), typeof(NumericUpDownControl),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

        public static readonly DependencyProperty MinimumProperty =
            DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(NumericUpDownControl),
                new PropertyMetadata(0.0));

        public static readonly DependencyProperty MaximumProperty =
            DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(NumericUpDownControl),
                new PropertyMetadata(1.0));

        public static readonly DependencyProperty StepProperty =
            DependencyProperty.Register(nameof(Step), typeof(double), typeof(NumericUpDownControl),
                new PropertyMetadata(0.01));

        public static readonly DependencyProperty FormatProperty =
            DependencyProperty.Register(nameof(Format), typeof(string), typeof(NumericUpDownControl),
                new PropertyMetadata("P0"));

        public double Value
        {
            get => (double)GetValue(ValueProperty);
            set => SetValue(ValueProperty, Coerce(value));
        }

        public double Minimum
        {
            get => (double)GetValue(MinimumProperty);
            set => SetValue(MinimumProperty, value);
        }

        public double Maximum
        {
            get => (double)GetValue(MaximumProperty);
            set => SetValue(MaximumProperty, value);
        }

        public double Step
        {
            get => (double)GetValue(StepProperty);
            set => SetValue(StepProperty, value);
        }

        public string Format
        {
            get => (string)GetValue(FormatProperty);
            set => SetValue(FormatProperty, value);
        }

        public NumericUpDownControl()
        {
            InitializeComponent();
            UpdateTextFromValue();
            PART_TextBox.LostFocus += PART_TextBox_LostFocus;
            PART_TextBox.PreviewKeyDown += PART_TextBox_PreviewKeyDown;
        }

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is NumericUpDownControl ctrl)
            {
                ctrl.UpdateTextFromValue();
            }
        }

        // Wrapper method wired from XAML
        private void TextBox_PreviewKeyDown(object sender, KeyEventArgs e) => PART_TextBox_PreviewKeyDown(sender, e);

        // Wrapper method wired from XAML
        private void TextBox_LostFocus(object sender, RoutedEventArgs e) => PART_TextBox_LostFocus(sender, e);

        // Wrapper method wired from XAML
        private void Up_Click(object sender, RoutedEventArgs e) => OnIncrement(sender, e);

        // Wrapper method wired from XAML
        private void Down_Click(object sender, RoutedEventArgs e) => OnDecrement(sender, e);

        private void PART_TextBox_PreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ParseAndSet(PART_TextBox.Text);
                e.Handled = true;
                Keyboard.ClearFocus();
                // ensure formatted text
                UpdateTextFromValue();
            }
        }

        private void PART_TextBox_LostFocus(object? sender, RoutedEventArgs e)
        {
            ParseAndSet(PART_TextBox.Text);
            UpdateTextFromValue();
        }

        private void OnIncrement(object sender, RoutedEventArgs e)
        {
            // increment value and update displayed text using the current format
            SetCurrentValue(ValueProperty, Coerce(Value + Step));
            UpdateTextFromValue();
        }

        private void OnDecrement(object sender, RoutedEventArgs e)
        {
            SetCurrentValue(ValueProperty, Coerce(Value - Step));
            UpdateTextFromValue();
        }

        private void ParseAndSet(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            text = text.Trim();
            // Support percent input like "26%" or "26 %"
            if (text.EndsWith("%"))
            {
                var num = text.Replace("%", "").Trim();
                if (double.TryParse(num, NumberStyles.Number, CultureInfo.CurrentCulture, out var v))
                {
                    SetCurrentValue(ValueProperty, Coerce(v / 100.0));
                    return;
                }
            }

            // Try parse with format-aware handling (if Format starts with 'P' treat plain number as percent-ish)
            if (!string.IsNullOrEmpty(Format) && Format.StartsWith("P", StringComparison.OrdinalIgnoreCase))
            {
                if (double.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var v2))
                {
                    // If user typed "0.26" assume that's the fraction; if typed "26" assume percent
                    if (Math.Abs(v2) > 1.0) v2 = v2 / 100.0;
                    SetCurrentValue(ValueProperty, Coerce(v2));
                    return;
                }
            }

            // fallback: try parse as fraction
            if (double.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var v3))
            {
                SetCurrentValue(ValueProperty, Coerce(v3));
                return;
            }

            // If nothing parsed, reset displayed text to current Value
            UpdateTextFromValue();
        }

        private double Coerce(double value)
        {
            if (double.IsNaN(value)) value = 0;
            if (value < Minimum) value = Minimum;
            if (value > Maximum) value = Maximum;
            return value;
        }

        private void UpdateTextFromValue()
        {
            try
            {
                // If format is percentage (P...), format value accordingly
                if (!string.IsNullOrEmpty(Format) && Format.StartsWith("P", StringComparison.OrdinalIgnoreCase))
                {
                    PART_TextBox.Text = Value.ToString(Format, CultureInfo.CurrentCulture);
                }
                else
                {
                    PART_TextBox.Text = Value.ToString(CultureInfo.CurrentCulture);
                }
            }
            catch
            {
                PART_TextBox.Text = Value.ToString(CultureInfo.CurrentCulture);
            }
        }
    }
}