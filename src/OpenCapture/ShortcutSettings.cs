using System;
using System.Globalization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OpenCapture
{
    internal enum ShortcutInputKind
    {
        Keyboard,
        Mouse
    }

    internal enum RecordingShortcutAvailability
    {
        AfterCaptureShortcut,
        AlwaysAvailable
    }

    internal sealed class ShortcutBinding : IEquatable<ShortcutBinding>
    {
        private readonly ShortcutInputKind kind;
        private readonly Keys keyCode;
        private readonly Keys modifiers;
        private readonly MouseButtons mouseButton;

        private ShortcutBinding(
            ShortcutInputKind bindingKind,
            Keys bindingKeyCode,
            Keys bindingModifiers,
            MouseButtons bindingMouseButton)
        {
            kind = bindingKind;
            keyCode = bindingKeyCode & Keys.KeyCode;
            modifiers = bindingModifiers & Keys.Modifiers;
            mouseButton = bindingMouseButton;
        }

        internal ShortcutInputKind Kind
        {
            get { return kind; }
        }

        internal Keys KeyCode
        {
            get { return keyCode; }
        }

        internal Keys Modifiers
        {
            get { return modifiers; }
        }

        internal MouseButtons MouseButton
        {
            get { return mouseButton; }
        }

        internal string DisplayText
        {
            get
            {
                if (kind == ShortcutInputKind.Mouse)
                {
                    if (mouseButton == MouseButtons.Left) return "Tap";
                    if (mouseButton == MouseButtons.Right) return "Right Click";
                    if (mouseButton == MouseButtons.Middle) return "Middle Click";
                    if (mouseButton == MouseButtons.XButton1) return "Mouse 4";
                    if (mouseButton == MouseButtons.XButton2) return "Mouse 5";
                    return "Mouse";
                }

                string result = string.Empty;
                if ((modifiers & Keys.Control) == Keys.Control) result += "Ctrl + ";
                if ((modifiers & Keys.Alt) == Keys.Alt) result += "Alt + ";
                if ((modifiers & Keys.Shift) == Keys.Shift) result += "Shift + ";
                return result + FriendlyKeyName(keyCode);
            }
        }

        internal static ShortcutBinding Keyboard(Keys modifiers, Keys keyCode)
        {
            Keys normalizedKey = keyCode & Keys.KeyCode;
            if (normalizedKey == Keys.None || IsModifierKey(normalizedKey))
            {
                throw new ArgumentException("A non-modifier key is required.", "keyCode");
            }
            return new ShortcutBinding(
                ShortcutInputKind.Keyboard,
                normalizedKey,
                modifiers,
                MouseButtons.None);
        }

        internal static ShortcutBinding Mouse(MouseButtons button)
        {
            if (button == MouseButtons.None)
            {
                throw new ArgumentException("A mouse button is required.", "button");
            }
            return new ShortcutBinding(ShortcutInputKind.Mouse, Keys.None, Keys.None, button);
        }

        internal bool Matches(Keys keyData)
        {
            return kind == ShortcutInputKind.Keyboard &&
                (keyData & Keys.KeyCode) == keyCode &&
                (keyData & Keys.Modifiers) == modifiers;
        }

        internal bool Matches(MouseButtons button)
        {
            return kind == ShortcutInputKind.Mouse && mouseButton == button;
        }

        internal string Serialize()
        {
            if (kind == ShortcutInputKind.Mouse)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "M:{0}",
                    (int)mouseButton);
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "K:{0}:{1}",
                (int)modifiers,
                (int)keyCode);
        }

        internal static bool TryParse(string value, out ShortcutBinding binding)
        {
            binding = null;
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            string[] parts = value.Split(':');
            int first;
            int second;
            if (parts.Length == 2 &&
                string.Equals(parts[0], "M", StringComparison.Ordinal) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out first))
            {
                MouseButtons button = (MouseButtons)first;
                if (button == MouseButtons.Left || button == MouseButtons.Right ||
                    button == MouseButtons.Middle || button == MouseButtons.XButton1 ||
                    button == MouseButtons.XButton2)
                {
                    binding = Mouse(button);
                    return true;
                }
            }

            if (parts.Length == 3 &&
                string.Equals(parts[0], "K", StringComparison.Ordinal) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out first) &&
                int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out second))
            {
                try
                {
                    binding = Keyboard((Keys)first, (Keys)second);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        public bool Equals(ShortcutBinding other)
        {
            return other != null &&
                kind == other.kind &&
                keyCode == other.keyCode &&
                modifiers == other.modifiers &&
                mouseButton == other.mouseButton;
        }

        public override bool Equals(object value)
        {
            return Equals(value as ShortcutBinding);
        }

        public override int GetHashCode()
        {
            return ((int)kind * 397) ^ (int)keyCode ^ (int)modifiers ^ (int)mouseButton;
        }

        private static bool IsModifierKey(Keys key)
        {
            return key == Keys.ControlKey || key == Keys.LControlKey || key == Keys.RControlKey ||
                key == Keys.ShiftKey || key == Keys.LShiftKey || key == Keys.RShiftKey ||
                key == Keys.Menu || key == Keys.LMenu || key == Keys.RMenu;
        }

        private static string FriendlyKeyName(Keys key)
        {
            if (key >= Keys.A && key <= Keys.Z)
            {
                return key.ToString();
            }
            if (key >= Keys.D0 && key <= Keys.D9)
            {
                return ((char)('0' + (key - Keys.D0))).ToString(CultureInfo.InvariantCulture);
            }
            if (key >= Keys.NumPad0 && key <= Keys.NumPad9)
            {
                return "Num " + ((int)(key - Keys.NumPad0)).ToString(CultureInfo.InvariantCulture);
            }

            switch (key)
            {
                case Keys.Space: return "Space";
                case Keys.Return: return "Enter";
                case Keys.Escape: return "Esc";
                case Keys.Back: return "Backspace";
                case Keys.Delete: return "Delete";
                case Keys.Oemcomma: return ",";
                case Keys.OemPeriod: return ".";
                case Keys.OemQuestion: return "/";
                case Keys.OemSemicolon: return ";";
                case Keys.OemQuotes: return "'";
                case Keys.OemOpenBrackets: return "[";
                case Keys.OemCloseBrackets: return "]";
                case Keys.OemPipe: return "\\";
                case Keys.OemMinus: return "-";
                case Keys.Oemplus: return "=";
                case Keys.Oemtilde: return "`";
                default: return key.ToString();
            }
        }
    }

    internal sealed class ShortcutSettings
    {
        private const string SettingsKeyPath = @"Software\OpenCapture";
        private const string CaptureValueName = "CaptureShortcut";
        private const string CopyColourValueName = "CopyColourShortcut";
        private const string CaptureZoneValueName = "CaptureZoneShortcut";
        private const string RecordingValueName = "RecordingShortcut";
        private const string RecordingControlValueName = "RecordingControlShortcut";
        private const string RecordingAvailabilityValueName = "RecordingShortcutAvailability";
        private const string DismissedValueName = "StartupWindowDismissedForever";

        internal static readonly ShortcutBinding DefaultCapture =
            ShortcutBinding.Keyboard(Keys.Alt, Keys.Q);
        internal static readonly ShortcutBinding DefaultCopyColour =
            ShortcutBinding.Keyboard(Keys.Control, Keys.C);
        internal static readonly ShortcutBinding DefaultCaptureZone =
            ShortcutBinding.Mouse(MouseButtons.Left);
        internal static readonly ShortcutBinding DefaultRecording =
            ShortcutBinding.Keyboard(Keys.Alt, Keys.Q);
        internal static readonly ShortcutBinding DefaultRecordingControl =
            ShortcutBinding.Keyboard(Keys.None, Keys.Space);
        internal const RecordingShortcutAvailability DefaultRecordingAvailability =
            RecordingShortcutAvailability.AfterCaptureShortcut;

        internal ShortcutSettings(
            ShortcutBinding captureShortcut,
            ShortcutBinding copyColourShortcut,
            ShortcutBinding captureZoneShortcut)
            : this(
                captureShortcut,
                copyColourShortcut,
                captureZoneShortcut,
                DefaultRecording,
                DefaultRecordingControl,
                DefaultRecordingAvailability)
        {
        }

        internal ShortcutSettings(
            ShortcutBinding captureShortcut,
            ShortcutBinding copyColourShortcut,
            ShortcutBinding captureZoneShortcut,
            ShortcutBinding recordingShortcut,
            RecordingShortcutAvailability recordingAvailability)
            : this(
                captureShortcut,
                copyColourShortcut,
                captureZoneShortcut,
                recordingShortcut,
                DefaultRecordingControl,
                recordingAvailability)
        {
        }

        internal ShortcutSettings(
            ShortcutBinding captureShortcut,
            ShortcutBinding copyColourShortcut,
            ShortcutBinding captureZoneShortcut,
            ShortcutBinding recordingShortcut,
            ShortcutBinding recordingControlShortcut,
            RecordingShortcutAvailability recordingAvailability)
        {
            if (captureShortcut == null || captureShortcut.Kind != ShortcutInputKind.Keyboard)
            {
                throw new ArgumentException("The capture shortcut must be a keyboard shortcut.", "captureShortcut");
            }
            if (copyColourShortcut == null)
            {
                throw new ArgumentNullException("copyColourShortcut");
            }
            if (captureZoneShortcut == null)
            {
                throw new ArgumentNullException("captureZoneShortcut");
            }
            if (recordingShortcut == null || recordingShortcut.Kind != ShortcutInputKind.Keyboard)
            {
                throw new ArgumentException("The recording shortcut must be a keyboard shortcut.", "recordingShortcut");
            }
            if (recordingControlShortcut == null ||
                recordingControlShortcut.Kind != ShortcutInputKind.Keyboard)
            {
                throw new ArgumentException(
                    "The recording control must be a keyboard shortcut.",
                    "recordingControlShortcut");
            }
            if (!Enum.IsDefined(typeof(RecordingShortcutAvailability), recordingAvailability))
            {
                throw new ArgumentOutOfRangeException("recordingAvailability");
            }

            CaptureShortcut = captureShortcut;
            CopyColourShortcut = copyColourShortcut;
            CaptureZoneShortcut = captureZoneShortcut;
            RecordingShortcut = recordingShortcut;
            RecordingControlShortcut = recordingControlShortcut;
            RecordingAvailability = recordingAvailability;
        }

        internal ShortcutBinding CaptureShortcut { get; private set; }
        internal ShortcutBinding CopyColourShortcut { get; private set; }
        internal ShortcutBinding CaptureZoneShortcut { get; private set; }
        internal ShortcutBinding RecordingShortcut { get; private set; }
        internal ShortcutBinding RecordingControlShortcut { get; private set; }
        internal RecordingShortcutAvailability RecordingAvailability { get; private set; }

        internal static ShortcutSettings CreateDefaults()
        {
            return new ShortcutSettings(
                DefaultCapture,
                DefaultCopyColour,
                DefaultCaptureZone,
                DefaultRecording,
                DefaultRecordingControl,
                DefaultRecordingAvailability);
        }

        internal static ShortcutSettings Load()
        {
            ShortcutBinding capture = DefaultCapture;
            ShortcutBinding colour = DefaultCopyColour;
            ShortcutBinding zone = DefaultCaptureZone;
            ShortcutBinding recording = DefaultRecording;
            ShortcutBinding recordingControl = DefaultRecordingControl;
            RecordingShortcutAvailability recordingAvailability = DefaultRecordingAvailability;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, false))
                {
                    if (key != null)
                    {
                        ShortcutBinding parsed;
                        if (ShortcutBinding.TryParse(key.GetValue(CaptureValueName) as string, out parsed) &&
                            parsed.Kind == ShortcutInputKind.Keyboard)
                        {
                            capture = parsed;
                        }
                        if (ShortcutBinding.TryParse(key.GetValue(CopyColourValueName) as string, out parsed))
                        {
                            colour = parsed;
                        }
                        if (ShortcutBinding.TryParse(key.GetValue(CaptureZoneValueName) as string, out parsed))
                        {
                            zone = parsed;
                        }
                        if (ShortcutBinding.TryParse(key.GetValue(RecordingValueName) as string, out parsed) &&
                            parsed.Kind == ShortcutInputKind.Keyboard)
                        {
                            recording = parsed;
                        }
                        if (ShortcutBinding.TryParse(
                            key.GetValue(RecordingControlValueName) as string,
                            out parsed) && parsed.Kind == ShortcutInputKind.Keyboard)
                        {
                            recordingControl = parsed;
                        }
                        object availabilityValue = key.GetValue(RecordingAvailabilityValueName);
                        if (availabilityValue is int && Enum.IsDefined(
                            typeof(RecordingShortcutAvailability),
                            (int)availabilityValue))
                        {
                            recordingAvailability = (RecordingShortcutAvailability)(int)availabilityValue;
                        }
                    }
                }
            }
            catch
            {
            }
            return new ShortcutSettings(
                capture,
                colour,
                zone,
                recording,
                recordingControl,
                recordingAvailability);
        }

        internal void Save()
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("Windows would not open OpenCapture settings.");
                }
                key.SetValue(CaptureValueName, CaptureShortcut.Serialize(), RegistryValueKind.String);
                key.SetValue(CopyColourValueName, CopyColourShortcut.Serialize(), RegistryValueKind.String);
                key.SetValue(CaptureZoneValueName, CaptureZoneShortcut.Serialize(), RegistryValueKind.String);
                key.SetValue(RecordingValueName, RecordingShortcut.Serialize(), RegistryValueKind.String);
                key.SetValue(
                    RecordingControlValueName,
                    RecordingControlShortcut.Serialize(),
                    RegistryValueKind.String);
                key.SetValue(
                    RecordingAvailabilityValueName,
                    (int)RecordingAvailability,
                    RegistryValueKind.DWord);
            }
        }

        internal ShortcutSettings WithBinding(int index, ShortcutBinding binding)
        {
            if (binding == null)
            {
                throw new ArgumentNullException("binding");
            }
            if (index == 0)
            {
                return new ShortcutSettings(
                    binding,
                    CopyColourShortcut,
                    CaptureZoneShortcut,
                    RecordingShortcut,
                    RecordingControlShortcut,
                    RecordingAvailability);
            }
            if (index == 1)
            {
                return new ShortcutSettings(
                    CaptureShortcut,
                    binding,
                    CaptureZoneShortcut,
                    RecordingShortcut,
                    RecordingControlShortcut,
                    RecordingAvailability);
            }
            if (index == 2)
            {
                return new ShortcutSettings(
                    CaptureShortcut,
                    CopyColourShortcut,
                    binding,
                    RecordingShortcut,
                    RecordingControlShortcut,
                    RecordingAvailability);
            }
            if (index == 3)
            {
                return new ShortcutSettings(
                    CaptureShortcut,
                    CopyColourShortcut,
                    CaptureZoneShortcut,
                    binding,
                    RecordingControlShortcut,
                    RecordingAvailability);
            }
            if (index == 4)
            {
                return new ShortcutSettings(
                    CaptureShortcut,
                    CopyColourShortcut,
                    CaptureZoneShortcut,
                    RecordingShortcut,
                    binding,
                    RecordingAvailability);
            }
            throw new ArgumentOutOfRangeException("index");
        }

        internal ShortcutSettings WithRecordingAvailability(
            RecordingShortcutAvailability availability)
        {
            return new ShortcutSettings(
                CaptureShortcut,
                CopyColourShortcut,
                CaptureZoneShortcut,
                RecordingShortcut,
                RecordingControlShortcut,
                availability);
        }

        internal static bool StartupWindowDismissedForever
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, false))
                    {
                        object value = key == null ? null : key.GetValue(DismissedValueName);
                        return value is int && (int)value != 0;
                    }
                }
                catch
                {
                    return false;
                }
            }
            set
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath))
                {
                    if (key == null)
                    {
                        throw new InvalidOperationException("Windows would not open OpenCapture settings.");
                    }
                    key.SetValue(DismissedValueName, value ? 1 : 0, RegistryValueKind.DWord);
                }
            }
        }
    }
}
