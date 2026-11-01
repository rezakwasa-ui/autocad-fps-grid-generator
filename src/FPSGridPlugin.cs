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

            // 1. Prompt for Property / Boundary Line
            PromptEntityOptions peoBoundary = new PromptEntityOptions("\nSelect Property / Boundary Line (or press ENTER to use column bounds): ");
            peoBoundary.AllowNone = true;
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

                // Compute drawing extents
                double minX = xCoords.First();
                double maxX = xCoords.Last();
                double minY = yCoords.First();
                double maxY = yCoords.Last();

                // If property line was picked, extend grids fully outside the property line
                if (boundaryExtents.HasValue)
                {
                    minX = Math.Min(minX, boundaryExtents.Value.MinPoint.X);
                    maxX = Math.Max(maxX, boundaryExtents.Value.MaxPoint.X);
                    minY = Math.Min(minY, boundaryExtents.Value.MinPoint.Y);
                    maxY = Math.Max(maxY, boundaryExtents.Value.MaxPoint.Y);
                }

                // Architectural FPS parameters in inches (1 ft = 12 in)
                double extension = 72.0;       // 6'-0" extension beyond boundary/columns
                double bubbleRad = 15.0;       // 1'-3" bubble radius (30" diameter)
                double textHt = 11.0;          // 11" text height
                double staggerDistance = 36.0; // 3'-0" outward shift per stagger tier
                double dimClearance = 48.0;    // 4'-0" spacing to dimension lines

                RegisterApp(tr, db);

                // Load or create Centerline linetype with visible scale
                ObjectId centerLtId = EnsureCenterLinetype(tr, db);

                EnsureLayer(tr, db, "S-GRID-LINE", 1, centerLtId); // Red + Centerline
                EnsureLayer(tr, db, "S-GRID-BUBL", 3, db.ContinuousLinetype); // Green
                EnsureLayer(tr, db, "S-GRID-TEXT", 7, db.ContinuousLinetype); // White
                EnsureLayer(tr, db, "S-GRID-DIMS", 2, db.ContinuousLinetype); // Yellow

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // Minimum distance required between bubbles to avoid collision (radius * 2 + 4" margin)
                double bubbleClearance = (bubbleRad * 2.0) + 4.0;

                // --- 1. Vertical Grids (1, 2, 3...) ---
                int[] vTiers = CalculateMultiTierStagger(xCoords, bubbleClearance);

                for (int i = 0; i < xCoords.Count; i++)
                {
                    double x = xCoords[i];
                    string tag = (i + 1).ToString();
                    int tier = vTiers[i]; // 0, 1, or 2

                    Point3d pStart = new Point3d(x, minY - extension, 0);
                    Point3d pEnd = new Point3d(x, maxY + extension, 0);

                    // Grid Line with Centerline Pattern
                    Line line = new Line(pStart, pEnd)
                    {
                        Layer = "S-GRID-LINE",
                        LinetypeId = centerLtId,
                        LinetypeScale = 48.0 // 48x scale for clear FPS dash-dot visibility
                    };
                    AttachMetadata(line, "VERTICAL", tag);
                    btr.AppendEntity(line);
                    tr.AddNewlyCreatedDBObject(line, true);

                    // Top Staggered Bubble
                    double topShift = tier * staggerDistance;
                    Point3d topBubbleCenter = new Point3d(x, maxY + extension + bubbleRad + topShift, 0);

                    if (tier > 0)
                    {
                        // Draw continuous extension line to staggered bubble
                        Line extLine = new Line(pEnd, new Point3d(x, maxY + extension + topShift, 0)) { Layer = "S-GRID-BUBL" };
                        btr.AppendEntity(extLine);
                        tr.AddNewlyCreatedDBObject(extLine, true);
                    }
                    CreateBubble(btr, tr, topBubbleCenter, bubbleRad, tag, textHt);

                    // Bottom Staggered Bubble
                    double botShift = tier * staggerDistance;
                    Point3d botBubbleCenter = new Point3d(x, minY - extension - bubbleRad - botShift, 0);

                    if (tier > 0)
                    {
                        Line extLine = new Line(pStart, new Point3d(x, minY - extension - botShift, 0)) { Layer = "S-GRID-BUBL" };
                        btr.AppendEntity(extLine);
                        tr.AddNewlyCreatedDBObject(extLine, true);
                    }
                    CreateBubble(btr, tr, botBubbleCenter, bubbleRad, tag, textHt);
                }

                // Vertical Dimensions (Placed above the highest staggered bubble tier)
                double maxTopY = maxY + extension + bubbleRad + (vTiers.Max() * staggerDistance);
                for (int i = 0; i < xCoords.Count - 1; i++)
                {
                    Point3d pt1 = new Point3d(xCoords[i], maxTopY, 0);
                    Point3d pt2 = new Point3d(xCoords[i + 1], maxTopY, 0);
                    Point3d dimPt = new Point3d(xCoords[i], maxTopY + dimClearance, 0);

                    RotatedDimension rotDim = new RotatedDimension(0.0, pt1, pt2, dimPt, "<>", db.Dimstyle)
                    {
                        Layer = "S-GRID-DIMS"
                    };
                    btr.AppendEntity(rotDim);
                    tr.AddNewlyCreatedDBObject(rotDim, true);
                }

                // --- 2. Horizontal Grids (A, B, C...) ---
                string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
                int[] hTiers = CalculateMultiTierStagger(yCoords, bubbleClearance);

                for (int j = 0; j < yCoords.Count; j++)
                {
                    double y = yCoords[j];
                    string tag = j < 26 ? alphabet[j].ToString() : "A" + (j - 25);
                    int tier = hTiers[j]; // 0, 1, or 2

                    Point3d pStart = new Point3d(minX - extension, y, 0);
                    Point3d pEnd = new Point3d(maxX + extension, y, 0);

                    Line line = new Line(pStart, pEnd)
                    {
                        Layer = "S-GRID-LINE",
                        LinetypeId = centerLtId,
                        LinetypeScale = 48.0
                    };
                    AttachMetadata(line, "HORIZONTAL", tag);
                    btr.AppendEntity(line);
                    tr.AddNewlyCreatedDBObject(line, true);

                    // Left Staggered Bubble
                    double leftShift = tier * staggerDistance;
                    Point3d leftBubbleCenter = new Point3d(minX - extension - bubbleRad - leftShift, y, 0);

                    if (tier > 0)
                    {
                        Line extLine = new Line(pStart, new Point3d(minX - extension - leftShift, y, 0)) { Layer = "S-GRID-BUBL" };
                        btr.AppendEntity(extLine);
                        tr.AddNewlyCreatedDBObject(extLine, true);
                    }
                    CreateBubble(btr, tr, leftBubbleCenter, bubbleRad, tag, textHt);

                    // Right Staggered Bubble
                    double rightShift = tier * staggerDistance;
                    Point3d rightBubbleCenter = new Point3d(maxX + extension + bubbleRad + rightShift, y, 0);

                    if (tier > 0)
                    {
                        Line extLine = new Line(pEnd, new Point3d(maxX + extension + rightShift, y, 0)) { Layer = "S-GRID-BUBL" };
                        btr.AppendEntity(extLine);
                        tr.AddNewlyCreatedDBObject(extLine, true);
                    }
                    CreateBubble(btr, tr, rightBubbleCenter, bubbleRad, tag, textHt);
                }

                // Horizontal Dimensions (Placed to the left of the outermost staggered bubble tier)
                double minLeftX = minX - extension - bubbleRad - (hTiers.Max() * staggerDistance);
                for (int j = 0; j < yCoords.Count - 1; j++)
                {
                    Point3d pt1 = new Point3d(
