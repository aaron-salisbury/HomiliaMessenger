using System;

namespace HomiliaMessenger.Presentation.Desktop.Base.Controls.RibbonControls;

public interface IRibbonProvider
{
    Type RibbonControlType { get; }
}
