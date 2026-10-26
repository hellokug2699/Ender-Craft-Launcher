// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;
using Ecl.App.ViewModels;
using Ecl.Configuration;

namespace Ecl.App.Selectors;

public sealed class OptionTemplateSelector : DataTemplateSelector
{
    public DataTemplate? BooleanOptionTemplate { get; set; }

    public DataTemplate? NumberOptionTemplate { get; set; }

    public DataTemplate? TextOptionTemplate { get; set; }

    public DataTemplate? PathOptionTemplate { get; set; }

    public DataTemplate? SelectOptionTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is not OptionEditorViewModel editor)
        {
            return TextOptionTemplate;
        }

        return editor.Definition.Kind switch
        {
            OptionKind.Boolean => BooleanOptionTemplate,
            OptionKind.Integer or OptionKind.Decimal => NumberOptionTemplate,
            OptionKind.Select => SelectOptionTemplate,
            OptionKind.Path => PathOptionTemplate,
            _ => TextOptionTemplate,
        };
    }
}
