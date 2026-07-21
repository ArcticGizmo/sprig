using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Sprig.App.ViewModels;

namespace Sprig.App;

/// <summary>Resolves a view-model to its view by naming convention (…ViewModels.XViewModel → …Views.XView).</summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control Build(object? data)
    {
        if (data is null) return new TextBlock { Text = "(null)" };

        var name = data.GetType().FullName!
            .Replace("ViewModels", "Views", StringComparison.Ordinal)
            .Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = Type.GetType(name);

        return type is not null
            ? (Control)Activator.CreateInstance(type)!
            : new TextBlock { Text = $"View not found: {name}" };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
