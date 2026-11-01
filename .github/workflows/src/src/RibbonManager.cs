using System;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Windows;

namespace FPSGridGenerator
{
    public class RibbonManager : IExtensionApplication
    {
        public void Initialize()
        {
            Autodesk.AutoCAD.ApplicationServices.Application.Idle += OnAutoCADIdle;
        }

        public void Terminate() { }

        private void OnAutoCADIdle(object sender, EventArgs e)
        {
            Autodesk.AutoCAD.ApplicationServices.Application.Idle -= OnAutoCADIdle;
            AddRibbonTab();
        }

        private void AddRibbonTab()
        {
            RibbonControl ribbon = ComponentManager.Ribbon;
            if (ribbon == null) return;

            string tabId = "ID_TAB_STRUCTURAL_TOOLS";
            RibbonTab existingTab = ribbon.FindTab(tabId);

            if (existingTab == null)
            {
                RibbonTab tab = new RibbonTab
                {
                    Title = "Structural Grids",
                    Id = tabId
                };
                ribbon.Tabs.Add(tab);

                RibbonPanelSource panelSource = new RibbonPanelSource { Title = "Grid Generation" };
                RibbonPanel panel = new RibbonPanel { Source = panelSource };
                tab.Panels.Add(panel);

                RibbonButton btnGen = new RibbonButton
                {
                    Text = "Auto Grid\n& Dims",
                    ShowText = true,
                    ShowImage = false,
                    Size = RibbonItemSize.Large,
                    CommandHandler = new RibbonCommandHandler("GENGRID\n")
                };
                panelSource.Items.Add(btnGen);

                RibbonButton btnEdit = new RibbonButton
                {
                    Text = "Edit\nGrid",
                    ShowText = true,
                    ShowImage = false,
                    Size = RibbonItemSize.Large,
                    CommandHandler = new RibbonCommandHandler("EDITGRID\n")
                };
                panelSource.Items.Add(btnEdit);
            }
        }
    }

    public class RibbonCommandHandler : System.Windows.Input.ICommand
    {
        private readonly string _command;
        public RibbonCommandHandler(string command) => _command = command;
        public bool CanExecute(object parameter) => true;
        public event EventHandler CanExecuteChanged { add { } remove { } }
        public void Execute(object parameter)
        {
            Autodesk.AutoCAD.ApplicationServices.Document doc = 
                Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc != null)
            {
                doc.SendStringToExecute(_command, true, false, false);
            }
        }
    }
}
