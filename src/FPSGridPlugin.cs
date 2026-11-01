using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.Windows;

[assembly: ExtensionApplication(typeof(FPSGridGenerator.FPSGridPlugin))]
[assembly: CommandClass(typeof(FPSGridGenerator.FPSGridPlugin))]

namespace FPSGridGenerator
{
    public class FPSGridPlugin : IExtensionApplication
    {
        private const string AppName = "FPS_GRID_METADATA";

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
            RibbonTab existingTab = ribbon.Tabs.FirstOrDefault(t => t.Id == tabId);

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
                    CommandHandler = new SimpleRelayCommand("GENGRID\n")
                };
                panelSource.Items.Add(btnGen);

                RibbonButton btnEdit = new RibbonButton
                {
                    Text = "Edit\nGrid",
                    ShowText = true,
                    ShowImage = false,
                    Size = RibbonItemSize.Large,
                    CommandHandler = new SimpleRelayCommand("EDITGRID\n")
                };
                panelSource.Items.Add(btnEdit);
            }
        }

        [CommandMethod("GENGRID")]
        public void GenerateGrids()
        {
            Document doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            // 1. Optional Property Line Selection
            PromptEntityOptions peoBoundary = new PromptEntityOptions("\nSelect Property / Boundary Line (Press ENTER to skip): ");
            peoBoundary.AllowNone = true;
            peoBoundary.SetRejectMessage("\nEntity must be a Polyline or Line.");
            peoBoundary.AddAllowedClass(typeof(Polyline), true);
            peoBoundary.AddAllowedClass(typeof(Polyline2d), true);
            peoBoundary.AddAllowedClass(typeof(Line), true);

            PromptEntityResult perBoundary = ed.GetEntity(peoBoundary);
            Extents3d? boundaryExtents = null;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (perBoundary.Status == PromptStatus.OK)
                {
                    Entity bEnt = tr.GetObject(perBoundary.ObjectId, OpenMode.ForRead) as Entity;
                    if (bEnt != null)
                    {
                        try { boundaryExtents = bEnt.GeometricExtents; } catch { }
                    }
                }

                // 2. Select Columns
                PromptKeywordOptions pko = new PromptKeywordOptions("\nSelect column extraction method [Layer/Selection]: ");
                pko.Keywords.Add("Layer");
                pko.Keywords.Add("Selection");
                pko.Keywords.Default = "Selection";

                PromptResult pkr = ed.GetKeywords(pko);
                if (pkr.Status != PromptStatus.OK) return;

                List<Point3d> colCenters = new List<Point3d>();

                if (pkr.StringResult == "Layer")
                {
                    PromptEntityOptions peo = new PromptEntityOptions("\nSelect a sample column to read its layer: ");
                    PromptEntityResult per = ed.GetEntity(peo);
                    if (per.Status != PromptStatus.OK) return;

                    Entity ent = tr.GetObject(per.ObjectId, OpenMode.ForRead) as Entity;
                    string targetLayer = ent.Layer;
                    ed.WriteMessage($"\nTarget layer: '{targetLayer}'");

                    TypedValue[] tv = new TypedValue[] { new TypedValue((int)DxfCode.LayerName, targetLayer) };
                    SelectionFilter filter = new SelectionFilter(tv);
                    PromptSelectionResult psr = ed.SelectAll(filter);
                    if (psr.Status == PromptStatus.OK)
                    {
                        colCenters = ExtractCenters(tr, psr.Value);
                    }
                }
                else
                {
                    PromptSelectionOptions pso = new PromptSelectionOptions();
                    pso.MessageForAdding = "\nSelect column entities (Press ENTER when done): ";
                    PromptSelectionResult psr = ed.GetSelection(pso);
                    if (psr.Status != PromptStatus.OK) return;

                    colCenters = ExtractCenters(tr, psr.Value);
                }

                if (colCenters.Count == 0)
                {
                    ed.WriteMessage("\nNo valid column entities found.");
                    return;
                }

                // 1-inch tolerance clustering for FPS coordinates
                double tolerance = 1.0;
                List<double> xCoords = ClusterCoordinates(colCenters.Select(p => p.X).ToList(), tolerance);
                List<double> yCoords = ClusterCoordinates(colCenters.Select(p => p.Y).ToList(), tolerance);

                xCoords.Sort();
                yCoords.Sort();

                double minX = xCoords.First();
                double maxX = xCoords.Last();
                double minY = yCoords.First();
                double maxY = yCoords.Last();

                // If property line is selected, extend bounds beyond boundary
                if (boundaryExtents.HasValue)
                {
                    minX = Math.Min(minX, boundaryExtents.Value.MinPoint.X);
                    maxX = Math.Max(maxX, boundaryExtents.Value.MaxPoint.X);
                    minY = Math.Min(minY, boundaryExtents.Value.MinPoint.Y);
                    maxY = Math.Max(maxY, boundaryExtents.Value.MaxPoint.Y);
                }

                // FPS Parameters (Inches: 1 ft = 12 in)
                double extension = 60.0;     // 5 ft outside boundary / outermost column
                double bubbleRad = 16.0;     // 1'-4" bubble radius
                double textHt = 11.0;        // 11" text height
                double staggerOffset = 36.0; // 3 ft stagger distance to eliminate bubble collision
                double dimOffset = 36.0;     // 3 ft distance to dimension strings

                RegisterApp(tr, db);
                ObjectId centerLinetypeId = EnsureLinetype(tr, db, "CENTER");
                EnsureLayer(tr, db, "S-GRID-LINE", 1, centerLinetypeId); // Red + Centerline
                EnsureLayer(tr, db, "S-GRID-BUBL", 3, db.ContinuousLinetype); // Green
                EnsureLayer(tr, db, "S-GRID-TEXT", 7, db.ContinuousLinetype); // White
                EnsureLayer(tr, db, "S-GRID-DIMS", 2, db.ContinuousLinetype); // Yellow

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // --- 1. Vertical Grids (1, 2, 3...) ---
                int[] vLevels = CalculateStaggerLevels(xCoords, bubbleRad * 2.3);

                for (int i = 0; i < xCoords.Count; i++)
                {
                    double x = xCoords[i];
                    string tag = (i + 1).ToString();
                    int lvl = vLevels[i];

                    // Main Centerline
                    Point3d pStart = new Point3d(x, minY - extension, 0);
                    Point3d pEnd = new Point3d(x, maxY + extension, 0);

                    Line line = new Line(pStart, pEnd)
                    {
                        Layer = "S-GRID-LINE",
                        LinetypeId = centerLinetypeId,
                        LinetypeScale = 24.0 // Scale for 1/4" = 1'-0" standard FPS readability
                    };
                    AttachMetadata(line, "VERTICAL", tag);
                    btr.AppendEntity(line);
                    tr.AddNewlyCreatedDBObject(line, true);

                    // Top Staggered Bubble & Jog Connector
                    double topShift = lvl * staggerOffset;
                    Point3d topBubblePt = new Point3d(x, maxY + extension + bubbleRad + topShift, 0);

                    if (lvl > 0)
                    {
                        Line jog = new Line(pEnd, new Point3d(x, maxY + extension + topShift, 0)) { Layer = "S-GRID-BUBL" };
                        btr.AppendEntity(jog);
                        tr.AddNewlyCreatedDBObject(jog, true);
                    }
                    CreateBubble(btr, tr, topBubblePt, bubbleRad, tag, textHt);

                    // Bottom Staggered Bubble & Jog Connector
                    double botShift = lvl * staggerOffset;
                    Point3d botBubblePt = new Point3d(x, minY - extension - bubbleRad - botShift, 0);

                    if (lvl > 0)
                    {
                        Line jog = new Line(pStart, new Point3d(x, minY - extension - botShift, 0)) { Layer = "S-GRID-BUBL" };
                        btr.AppendEntity(jog);
                        tr.AddNewlyCreatedDBObject(jog, true);
                    }
                    CreateBubble(btr, tr, botBubblePt, bubbleRad, tag, textHt);
                }

                // Vertical Bay Dimensions (Placed above highest staggered bubble)
                double maxTopY = maxY + extension + bubbleRad + (vLevels.Max() * staggerOffset);
                for (int i = 0; i < xCoords.Count - 1; i++)
                {
                    Point3d pt1 = new Point3d(xCoords[i], maxTopY, 0);
                    Point3d pt2 = new Point3d(xCoords[i + 1], maxTopY, 0);
                    Point3d dimLinePt = new Point3d(xCoords[i], maxTopY + dimOffset, 0);

                    RotatedDimension rotDim = new RotatedDimension(0.0, pt1, pt2, dimLinePt, "<>", db.Dimstyle)
                    {
                        Layer = "S-GRID-DIMS"
                    };
                    btr.AppendEntity(rotDim);
                    tr.AddNewlyCreatedDBObject(rotDim, true);
                }

                // --- 2. Horizontal Grids (A, B, C...) ---
                string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
                int[] hLevels = CalculateStaggerLevels(yCoords, bubbleRad * 2.3);

                for (int j = 0; j < yCoords.Count; j++)
                {
                    double y = yCoords[j];
                    string tag = j < 26 ? alphabet[j].ToString() : "A" + (j - 25);
                    int lvl = hLevels[j];

                    // Main Centerline
                    Point3d pStart = new Point3d(minX - extension, y, 0);
                    Point3d pEnd = new Point3d(maxX + extension, y, 0);

                    Line line = new Line(pStart, pEnd)
                    {
                        Layer = "S-GRID-LINE",
                        LinetypeId = centerLinetypeId,
                        LinetypeScale = 24.0
                    };
                    AttachMetadata(line, "HORIZONTAL", tag);
                    btr.AppendEntity(line);
                    tr.AddNewlyCreatedDBObject(line, true);

                    // Left Staggered Bubble & Jog
                    double leftShift = lvl * staggerOffset;
                    Point3d leftBubblePt = new Point3d(minX - extension - bubbleRad - leftShift, y, 0);

                    if (lvl > 0)
                    {
                        Line jog = new Line(pStart, new Point3d(minX - extension - leftShift, y, 0)) { Layer = "S-GRID-BUBL" };
                        btr.AppendEntity(jog);
                        tr.AddNewlyCreatedDBObject(jog, true);
                    }
                    CreateBubble(btr, tr, leftBubblePt, bubbleRad, tag, textHt);

                    // Right Staggered Bubble & Jog
                    double rightShift = lvl * staggerOffset;
                    Point3d rightBubblePt = new Point3d(maxX + extension + bubbleRad + rightShift, y, 0);

                    if (lvl > 0)
                    {
                        Line jog = new Line(pEnd, new Point3d(maxX + extension + rightShift, y, 0)) { Layer = "S-GRID-BUBL" };
                        btr.AppendEntity(jog);
                        tr.AddNewlyCreatedDBObject(jog, true);
                    }
                    CreateBubble(btr, tr, rightBubblePt, bubbleRad, tag, textHt);
                }

                // Horizontal Bay Dimensions (Placed to left of outermost bubble)
                double minLeftX = minX - extension - bubbleRad - (hLevels.Max() * staggerOffset);
                for (int j = 0; j < yCoords.Count - 1; j++)
                {
                    Point3d pt1 = new Point3d(minLeftX, yCoords[j], 0);
                    Point3d pt2 = new Point3d(minLeftX, yCoords[j + 1], 0);
                    Point3d dimLinePt = new Point3d(minLeftX - dimOffset, yCoords[j], 0);

                    RotatedDimension rotDim = new RotatedDimension(Math.PI / 2.0, pt1, pt2, dimLinePt, "<>", db.Dimstyle)
                    {
                        Layer = "S-GRID-DIMS"
                    };
                    btr.AppendEntity(rotDim);
                    tr.AddNewlyCreatedDBObject(rotDim, true);
                }

                tr.Commit();
                ed.WriteMessage($"\nGenerated non-overlapping centerline grids for {xCoords.Count} X-Bays and {yCoords.Count} Y-Bays outside property limits.");
            }
        }

        [CommandMethod("EDITGRID")]
        public void EditGrid()
        {
            Document doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            PromptEntityOptions peo = new PromptEntityOptions("\nSelect a grid line to edit: ");
            peo.SetRejectMessage("\nMust be a line entity.");
            peo.AddAllowedClass(typeof(Line), true);

            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK) return;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Line line = (Line)tr.GetObject(per.ObjectId, OpenMode.ForRead);
                ResultBuffer rb = line.GetXDataForApplication(AppName);

                if (rb == null)
                {
                    ed.WriteMessage("\nNo grid data attached to this line.");
                    return;
                }

                TypedValue[] tv = rb.AsArray();
                string curOrientation = tv[1].Value.ToString();
                string curTag = tv[2].Value.ToString();

                PromptStringOptions pso = new PromptStringOptions($"\nEnter new grid label [{curTag}]: ");
                PromptResult pr = ed.GetString(pso);
                if (pr.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(pr.StringResult))
                {
                    line.UpgradeOpen();
                    AttachMetadata(line, curOrientation, pr.StringResult.Trim().ToUpper());
                    ed.WriteMessage($"\nLabel updated to {pr.StringResult.Trim().ToUpper()}.");
                }
                tr.Commit();
            }
        }

        // Computes 0 or 1 stagger level whenever adjacent axes are closer than clearanceThreshold
        private int[] CalculateStaggerLevels(List<double> coords, double clearanceThreshold)
        {
            int n = coords.Count;
            int[] levels = new int[n];
            for (int i = 1; i < n; i++)
            {
                if (Math.Abs(coords[i] - coords[i - 1]) < clearanceThreshold)
                {
                    levels[i] = (levels[i - 1] == 0) ? 1 : 0;
                }
                else
                {
                    levels[i] = 0;
                }
            }
            return levels;
        }

        private List<Point3d> ExtractCenters(Transaction tr, SelectionSet ss)
        {
            List<Point3d> centers = new List<Point3d>();
            foreach (SelectedObject so in ss)
            {
                if (so == null) continue;
                Entity ent = tr.GetObject(so.ObjectId, OpenMode.ForRead) as Entity;
                if (ent == null) continue;

                if (ent is Circle circle)
                {
                    centers.Add(circle.Center);
                }
                else
                {
                    try
                    {
                        Extents3d ext = ent.GeometricExtents;
                        centers.Add(new Point3d((ext.MinPoint.X + ext.MaxPoint.X) / 2.0, (ext.MinPoint.Y + ext.MaxPoint.Y) / 2.0, 0));
                    }
                    catch { }
                }
            }
            return centers;
        }

        private List<double> ClusterCoordinates(List<double> raw, double tol)
        {
            List<double> clustered = new List<double>();
            foreach (double val in raw.OrderBy(v => v))
            {
                if (!clustered.Any(c => Math.Abs(c - val) <= tol))
                    clustered.Add(val);
            }
            return clustered;
        }

        private void CreateBubble(BlockTableRecord btr, Transaction tr, Point3d center, double rad, string text, double textHt)
        {
            Circle c = new Circle(center, Vector3d.ZAxis, rad) { Layer = "S-GRID-BUBL" };
            btr.AppendEntity(c);
            tr.AddNewlyCreatedDBObject(c, true);

            DBText t = new DBText
            {
                Position = center,
                Height = textHt,
                TextString = text,
                Layer = "S-GRID-TEXT",
                HorizontalMode = TextHorizontalMode.TextCenter,
                VerticalMode = TextVerticalMode.TextVerticalMid,
                AlignmentPoint = center
            };
            btr.AppendEntity(t);
            tr.AddNewlyCreatedDBObject(t, true);
        }

        private void AttachMetadata(Line line, string orient, string tag)
        {
            line.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, orient),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, tag)
            );
        }

        private void RegisterApp(Transaction tr, Database db)
        {
            RegAppTable rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (!rat.Has(AppName))
            {
                rat.UpgradeOpen();
                RegAppTableRecord rtr = new RegAppTableRecord { Name = AppName };
                rat.Add(rtr);
                tr.AddNewlyCreatedDBObject(rtr, true);
            }
        }

        private ObjectId EnsureLinetype(Transaction tr, Database db, string ltName)
        {
            LinetypeTable lt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            if (lt.Has(ltName)) return lt[ltName];

            try
            {
                db.LoadLineTypeFile(ltName, "acad.lin");
                if (lt.Has(ltName)) return lt[ltName];
            }
            catch { }

            return db.ContinuousLinetype;
        }

        private void EnsureLayer(Transaction tr, Database db, string name, short colorIdx, ObjectId linetypeId)
        {
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!lt.Has(name))
            {
                lt.UpgradeOpen();
                LayerTableRecord ltr = new LayerTableRecord
                {
                    Name = name,
                    Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, colorIdx),
                    LinetypeObjectId = linetypeId
                };
                lt.Add(ltr);
                tr.AddNewlyCreatedDBObject(ltr, true);
            }
            else
            {
                LayerTableRecord ltr = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForWrite);
                ltr.LinetypeObjectId = linetypeId;
            }
        }
    }

    public class SimpleRelayCommand : System.Windows.Input.ICommand
    {
        private readonly string _cmd;
        public SimpleRelayCommand(string cmd) => _cmd = cmd;
        public bool CanExecute(object parameter) => true;
        public event EventHandler CanExecuteChanged { add { } remove { } }
        public void Execute(object parameter)
        {
            Document doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc != null)
            {
                doc.SendStringToExecute(_cmd, true, false, false);
            }
        }
    }
}
