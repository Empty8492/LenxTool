using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using LenxTool.App.Controls;

namespace LenxTool.App.Tests.Controls;

public sealed class PasswordBoxAssistantTests
{
    [Fact]
    public void PasswordChangeKeepsTwoWayBindingAndUpdatesSource()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var source = new PasswordSource();
                var passwordBox = new PasswordBox();
                var binding = new Binding(nameof(PasswordSource.Value))
                {
                    Source = source,
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                };
                BindingOperations.SetBinding(
                    passwordBox,
                    PasswordBoxAssistant.BoundPasswordProperty,
                    binding);

                passwordBox.Password = "sk-test-not-a-real-key";

                Assert.Equal("sk-test-not-a-real-key", source.Value);
                Assert.NotNull(BindingOperations.GetBindingExpression(
                    passwordBox,
                    PasswordBoxAssistant.BoundPasswordProperty));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private sealed class PasswordSource : DependencyObject
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            nameof(Value),
            typeof(string),
            typeof(PasswordSource),
            new PropertyMetadata(string.Empty));

        public string Value
        {
            get => (string)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }
    }
}
