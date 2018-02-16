﻿using System;using System.Collections.Generic;using System.ComponentModel;using System.IO;using System.Linq;using System.Threading.Tasks;using System.Windows;using System.Windows.Input;using AcManager.Tools;using AcManager.Tools.Helpers;using AcManager.Tools.Helpers.DirectInput;using AcManager.Tools.Miscellaneous;using AcTools.Utils;using AcTools.Utils.Helpers;using FirstFloor.ModernUI;using FirstFloor.ModernUI.Commands;using FirstFloor.ModernUI.Helpers;using FirstFloor.ModernUI.Presentation;using FirstFloor.ModernUI.Serialization;using FirstFloor.ModernUI.Windows;using FirstFloor.ModernUI.Windows.Media;using JetBrains.Annotations;using Newtonsoft.Json;using Newtonsoft.Json.Linq;using Clipboard = System.Windows.Clipboard;using TextBox = System.Windows.Controls.TextBox;namespace AcManager.Pages.Dialogs {    public partial class ControllerDefinitionsDialog {        #region Suggestions        public class SpecialSymbol : NotifyPropertyChanged {            public SpecialSymbol(string symbol, string hint) {                Symbol = symbol;                Hint = hint;            }            [NotNull]            public string Symbol { get; }            [NotNull]            public string Hint { get; }            public override string ToString() {                return Symbol;            }        }        public static SpecialSymbol[] SpecialSymbols { get; } = {            new SpecialSymbol("△", "Triangle, usually the top button in buttons arranged in rhombus shape"),            new SpecialSymbol("☐", "Square, usually the left button in buttons arranged in rhombus shape"),            new SpecialSymbol("○", "Circle, usually the right button in buttons arranged in rhombus shape"),            new SpecialSymbol("×", "Cross, usually the bottom button in buttons arranged in rhombus shape"),            new SpecialSymbol("⏎", "Down-left arrow"),            new SpecialSymbol("−", "Proper minus sign"),            new SpecialSymbol("↻", "Clockwise rotation arrow"),            new SpecialSymbol("↺", "Counter-clockwise rotation arrow"),            new SpecialSymbol("❮", "Arrow for the left paddle"),            new SpecialSymbol("❯", "Arrow for the right paddle"),        };        public class RecommendedName : NotifyPropertyChanged {            public RecommendedName(string shortName, string fullName) {                ShortName = shortName;                FullName = fullName?.ToSentenceMember();            }            [NotNull]            public string ShortName { get; }            [CanBeNull]            public string FullName { get; }            public override string ToString() {                return ShortName;            }        }        public static RecommendedName[] RecommendedAxisNames { get; } = {            new RecommendedName(ToolsStrings.Controls_Steer, "Steering wheel"),            new RecommendedName(ToolsStrings.Controls_Clutch, "Clutch pedal"),            new RecommendedName(ToolsStrings.Controls_Gas, "Gas pedal"),            new RecommendedName(ToolsStrings.Controls_Brake, "Brake pedal"),        };        public class RecommendedButtonName : RecommendedName {            public RecommendedButtonName(string shortName, string fullName = null)                    : base(shortName, fullName ?? string.Format(ToolsStrings.Input_Button, shortName)) { }        }        public static RecommendedButtonName[] RecommendedButtonsNames { get; } = {            new RecommendedButtonName("△"),            new RecommendedButtonName("☐"),            new RecommendedButtonName("○"),            new RecommendedButtonName("×"),            new RecommendedButtonName("❮", "Left paddle"),            new RecommendedButtonName("❯", "Right paddle"),            new RecommendedButtonName("SE", "Select button"),            new RecommendedButtonName("ST", "Start button"),            new RecommendedButtonName("PS", "PlayStation button"),            new RecommendedButtonName("G−", "Previous gear"),            new RecommendedButtonName("G+", "Next gear"),            new RecommendedButtonName("G1", "Gear 1"),            new RecommendedButtonName("G2", "Gear 2"),            new RecommendedButtonName("G3", "Gear 3"),            new RecommendedButtonName("G4", "Gear 4"),            new RecommendedButtonName("G5", "Gear 5"),            new RecommendedButtonName("G6", "Gear 6"),            new RecommendedButtonName("G7", "Gear 7"),            new RecommendedButtonName("GR", "Gear R"),            new RecommendedButtonName("+"),            new RecommendedButtonName("−"),            new RecommendedButtonName("⏎"),            new RecommendedButtonName("H", "Horn button"),            new RecommendedButtonName("L1"),            new RecommendedButtonName("R1"),            new RecommendedButtonName("L2"),            new RecommendedButtonName("R2"),            new RecommendedButtonName("L3"),            new RecommendedButtonName("R3"),        };        public static RecommendedName[] RecommendedPovNames { get; } = {            new RecommendedName("P", "POV"),            new RecommendedName("P1", "POV1"),            new RecommendedName("P2", "POV2"),            new RecommendedName("PL", "POV left"),            new RecommendedName("PR", "POV right"),        };        #endregion        public ControllerDefinitionsDialog(DirectInputDevice device) {            DataContext = new ViewModel(device);            InitializeComponent();            Buttons = new[] {                CreateCloseDialogButton("Save", true, false, MessageBoxResult.OK, Model.SaveCommand),                CancelButton            };        }        private ViewModel Model => (ViewModel)DataContext;        public abstract class InputItemBase : NotifyPropertyChanged {            public string DefaultShortName { get; }            public abstract int Id { get; }            public abstract bool IsVisible { get; set; }            protected InputItemBase(string defaultShortName, string shortName, string defaultFullName, string fullName) {                DefaultShortName = defaultShortName;                ShortName = shortName;                if (DefaultFullName == fullName) {                    FullName = null;                } else {                    DefaultFullName = defaultFullName;                    FullName = fullName == defaultFullName ? null : fullName;                }                IsCustom = DefaultShortName != ShortName || DefaultFullName != FullName;            }            private bool _isCustom;            public bool IsCustom {                get => _isCustom;                set => Apply(value, ref _isCustom);            }            private string _shortName;            [CanBeNull]            public string ShortName {                get => _shortName;                set {                    if (Equals(value, _shortName)) return;                    var oldValue = _shortName;                    _shortName = value;                    OnPropertyChanged();                    DefaultFullName = GetFullNamePlaceholder(value ?? "");                    IsCustom = true;                    var previousRecommended = GetRecommendedNames()?.FirstOrDefault(x => x.ShortName == oldValue);                    if (FullName == null || FullName == DefaultFullName || FullName == previousRecommended?.FullName.ToTitle()) {                        var currentRecommended = GetRecommendedNames()?.FirstOrDefault(x => x.ShortName == value);                        if (currentRecommended != null) {                            var fullName = currentRecommended.FullName.ToTitle();                            FullName = fullName != DefaultFullName ? fullName : null;                        }                    }                }            }            [NotNull]            protected abstract string GetFullNamePlaceholder([NotNull] string value);            [CanBeNull]            protected abstract IEnumerable<RecommendedName> GetRecommendedNames();            private string _defaultFullName;            public string DefaultFullName {                get => _defaultFullName;                private set => Apply(value, ref _defaultFullName);            }            private string _fullName;            [CanBeNull]            public string FullName {                get => _fullName;                set {                    if (Equals(value, _fullName)) return;                    _fullName = value;                    OnPropertyChanged();                    IsCustom = true;                }            }        }        public abstract class InputItemBase<T> : InputItemBase where T : IInputProvider {            public T Input { get; }            public override int Id => Input.Id;            protected InputItemBase(T input, bool isVisible) : base(input.DefaultShortName, input.ShortName, input.DefaultDisplayName, input.DisplayName) {                _isVisible = isVisible;                Input = input;            }            private bool _isVisible;            public override bool IsVisible {                get => _isVisible;                set {                    if (Equals(value, _isVisible)) return;                    _isVisible = value;                    OnPropertyChanged();                    IsCustom = true;                }            }        }        public class InputAxleItem : InputItemBase<DirectInputAxle> {            protected override string GetFullNamePlaceholder(string value) {                return string.Format(ToolsStrings.Input_Axle, value);            }            protected override IEnumerable<RecommendedName> GetRecommendedNames() {                return RecommendedAxisNames;            }            public InputAxleItem(DirectInputAxle input, bool isVisible) : base(input, isVisible) { }        }        public class InputButtonItem : InputItemBase<DirectInputButton> {            protected override string GetFullNamePlaceholder(string value) {                return string.Format(ToolsStrings.Input_Button, value);            }            protected override IEnumerable<RecommendedName> GetRecommendedNames() {                return RecommendedButtonsNames;            }            public InputButtonItem(DirectInputButton input, bool isVisible) : base(input, isVisible) { }        }        public class InputPovItem : InputItemBase {            public Dictionary<string, DirectInputPov> Inputs { get; }            public DirectInputPov InputLeft { get; }            public DirectInputPov InputUp { get; }            public DirectInputPov InputRight { get; }            public DirectInputPov InputDown { get; }            protected override IEnumerable<RecommendedName> GetRecommendedNames() {                return RecommendedPovNames;            }            private static string ToCommon(string s) {                return s.ApartFromLast("←").TrimEnd();            }            public InputPovItem(DirectInputPov pov, IEnumerable<DirectInputPov> inputs)                    : base(ToCommon(pov.DefaultShortName), ToCommon(pov.ShortName),                            ToCommon(pov.DefaultDisplayName), ToCommon(pov.DisplayName)) {                Inputs = inputs.ToDictionary(x => x.DisplayName.LastOrDefault().ToString(), x => x);                InputLeft = Inputs["←"];                InputUp = Inputs["↑"];                InputRight = Inputs["→"];                InputDown = Inputs["↓"];                Id = pov.Id;            }            public override int Id { get; }            public override bool IsVisible { get; set; } = true;            protected override string GetFullNamePlaceholder(string value) {                return DirectInputPov.ToFullName(value);            }        }        private class ViewModel : NotifyPropertyChanged {            public DirectInputDevice Device { get; }            public ChangeableObservableCollection<InputAxleItem> Axis { get; }            public ChangeableObservableCollection<InputButtonItem> Buttons { get; }            public ChangeableObservableCollection<InputPovItem> Povs { get; }            public string DefaultDeviceName { get; }            private string _deviceName;            public string DeviceName {                get => _deviceName;                set => Apply(value, ref _deviceName);            }            public ViewModel(DirectInputDevice device) {                Device = device;                DefaultDeviceName = DirectInputDevice.FixDisplayName(device.DisplayName);                DeviceName = DefaultDeviceName;                Axis = new ChangeableObservableCollection<InputAxleItem>(device.Axis.Select(x =>                        new InputAxleItem(x, device.VisibleAxis.Contains(x))));                Buttons = new ChangeableObservableCollection<InputButtonItem>(device.Buttons.Select(x =>                        new InputButtonItem(x, device.VisibleButtons.Contains(x))));                Povs = new ChangeableObservableCollection<InputPovItem>(device.Povs.Where(x => x.Direction == DirectInputPovDirection.Left).Select(x =>                        new InputPovItem(x, device.Povs.Where(y => y.Id == x.Id))));                UpdateIsModified();                Axis.ItemPropertyChanged += OnItemPropertyChanged;                Buttons.ItemPropertyChanged += OnItemPropertyChanged;                Povs.ItemPropertyChanged += OnItemPropertyChanged;            }            private void OnItemPropertyChanged(object sender, PropertyChangedEventArgs e) {                if (e.PropertyName == nameof(InputItemBase.IsCustom)) {                    UpdateIsModified();                }            }            private bool _isModified;            public bool IsModified {                get => _isModified;                private set {                    if (Equals(value, _isModified)) return;                    _isModified = value;                    OnPropertyChanged();                    _saveCommand?.RaiseCanExecuteChanged();                }            }            private void UpdateIsModified() {                IsModified = Axis.Any(x => x.IsCustom) || Buttons.Any(x => x.IsCustom) || Povs.Any(x => x.IsCustom)                        || !string.IsNullOrWhiteSpace(DeviceName) && DeviceName != DefaultDeviceName;            }            private DelegateCommand _saveCommand;            public DelegateCommand SaveCommand => _saveCommand ?? (_saveCommand = new DelegateCommand(() => {                var localized = new Dictionary<string, string>();                foreach (var pair in ToolsStrings.ResourceManager.Enumerate()) {                    localized[pair.Value] = pair.Key;                }                var jObject = new JObject();                SetNonDefault(jObject, "name", DeviceName.Trim(), DefaultDeviceName);                SetNonDefault(jObject, "axis", Save(Axis));                SetNonDefault(jObject, "pov", Save(Povs));                SetNonDefault(jObject, "buttons", Save(Buttons));                var json = $"// {Device.Device.InstanceName}\r\n{jObject.ToString(Formatting.Indented)}";                var fileName = $"{Device.Device.ProductGuid.ToString().ToUpperInvariant()}.json";                var file = FilesStorage.Instance.GetFilename(FilesStorage.DataUserDirName, ContentCategory.Controllers, fileName);                using (var recycle = FileUtils.RecycleOriginal(file)) {                    File.WriteAllText(recycle.Filename, json);                }                Device.RefreshDescription();                if (Stored.GetValue("ControllerDefinitionsDialog.ShareNames", false)) {                    Task.Run(() => {                        try {                            AppReporter.SendData(fileName, json, $"Definitions for {Device.Device.InstanceName} ({DeviceName})");                            Toast.Show("Data sent", AppStrings.About_ReportAnIssue_Sent_Message);                        } catch (Exception e) {                            Logging.Error(e.Message);                        }                    });                }                void SetNonDefault(JObject baseObject, string key, JToken token, JToken defaultValue = null) {                    if (token == defaultValue || token == null || token.Type == JTokenType.String && (string)token == "") return;                    baseObject[key] = token;                }                JToken Save(IEnumerable<InputItemBase> items) {                    var list = items.ToList();                    if (list.All(x => !x.IsVisible || !x.IsCustom) && !list.SkipWhile(x => x.IsVisible).SkipWhile(x => !x.IsVisible).Any()) {                        return list.All(x => x.IsVisible) || list.Count == 0 ? (JToken)null : list.Count(x => x.IsVisible);                    }                    if (list.All(x => x.IsVisible)) {                        return new JArray(list.Select(GetName));                    }                    return new JObject(list.Where(x => x.IsVisible).Select(x => new JProperty(x.Id.As<string>(), GetName(x))));                }                string GetName(InputItemBase item) {                    var shortName = (string.IsNullOrWhiteSpace(item.ShortName) ? item.DefaultShortName : item.ShortName).Trim();                    var fullName = (string.IsNullOrWhiteSpace(item.FullName) ? item.DefaultFullName : item.FullName).Trim();                    if (localized.TryGetValue(shortName, out var shortNameKey)) {                        shortName = $@"{{ToolsStrings.{shortNameKey}}}";                    }                    if (localized.TryGetValue(fullName, out var fullNameKey)) {                        fullName = $@"{{ToolsStrings.{fullNameKey}}}";                    }                    return fullName == item.DefaultFullName.Trim() ? shortName : $@"{shortName};{fullName}";                }            }, () => IsModified));        }        private void OnCharacterButtonClick(object sender, MouseButtonEventArgs e) {            if (((FrameworkElement)sender).DataContext is SpecialSymbol symbol) {                e.Handled = true;                var value = symbol.Symbol;                var focused = this.FindVisualChildren<TextBox>().FirstOrDefault(x => x.IsKeyboardFocused || x.IsKeyboardFocusWithin || x.IsFocused);                if (focused == null) {                    Clipboard.SetText(value);                    Toast.Show("Copied to clipboard", $"Symbol “{value}” is copied to the clipboard");                    return;                }                if (focused.MaxLength == 3) {                    focused.Clear();                } else {                    var leftToLimit = focused.MaxLength - focused.Text.Length;                    if (leftToLimit < value.Length) {                        value = focused.Text.Substring(value.Length - leftToLimit) + value;                        focused.Clear();                    }                }                focused.AppendText(value);                focused.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();            }        }    }}