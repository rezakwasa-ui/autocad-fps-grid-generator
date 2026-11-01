using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.EditorInput;

[assembly: CommandClass(typeof(FPSGridGenerator.GridCommands))]

namespace FPSGridGenerator
{
    public class GridCommands
    {
        private const string AppName = "FPS_GRID_METADATA";

        [CommandMethod("GENGRID")]
        public void GenerateGrids()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            PromptKeywordOptions pko = new PromptKeywordOptions("\nSelect column extraction method [Layer/Selection]: ");
            pko.Keywords.Add("Layer");
            pko.Keywords.Add("Selection");
            pko.Keywords.Default = "Selection";

            PromptResult pkr = ed.GetKeywords(pko);
            if (pkr.Status != PromptStatus.OK) return;

            List<Point3d> colCenters = new List<Point3d>();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (pkr.StringResult == "Layer")
                {
                    PromptEntityOptions peo = new PromptEntityOptions("\nSelect a sample column to read its layer: ");
                    PromptEntityResult per = ed.GetEntity(peo);
                    if (per.Status != PromptStatus.OK) return;

                    Entity ent = tr.GetObject(per.ObjectId, OpenMode.ForRead) as Entity;
                    string targetLayer = ent.Layer;
                    ed.WriteMessage($"\nTarget layer: '{targetLayer}'");

                    TypedValue[] tv = new TypedValue[]
                    {
                        new TypedValue((int)DxfCode.LayerName, targetLayer)
                    };
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

                double tolerance = 1.0; 
                List<double> xCoords = ClusterCoordinates(colCenters.Select(p => p.X).ToList(), tolerance);
                List<double> yCoords = ClusterCoordinates(colCenters.Select(p => p.Y).ToList(), tolerance);

                xCoords.Sort();
                yCoords.Sort();

                double minX = xCoords.First();
                double maxX = xCoords.Last();
                double minY = yCoords.First();
                double maxY = yCoords.Last();

                double extension = 48.0;     // 4 ft overhang
                double bubbleRad = 18.0;     // 1.5 ft bubble radius
                double textHt = 12.0;        // 1.0 ft text height
                double dimOffset = 36.0;     // 3 ft offset for bay dimensions

                RegisterApp(tr, db);
                EnsureLayer(tr, db, "S-GRID-LINE", 1);
                EnsureLayer(tr, db, "S-GRID-BUBL", 3);
                EnsureLayer(tr, db, "S-GRID-TEXT", 7);
                EnsureLayer(tr, db, "S-GRID-DIMS", 2);

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                for (int i = 0; i < xCoords.Count; i++)
                {
                    double x = xCoords[i];
                    string tag = (i + 1).ToString();

                    Point3d p1 = new Point3d(x, minY - extension, 0);
                    Point3d p2 = new Point3d(x, maxY + extension, 0);

                    Line line = new Line(p1, p2) { Layer = "S-GRID-LINE" };
                    AttachMetadata(line, "VERTICAL", tag);
                    btr.AppendEntity(line);
                    tr.AddNewlyCreatedDBObject(line, true);

                    CreateBubble(btr, tr, new Point3d(x, maxY + extension + bubbleRad, 0), bubbleRad, tag, textHt);
                    CreateBubble(btr, tr, new Point3d(x, minY - extension - bubbleRad, 0), bubbleRad, tag, textHt);
                }

                for (int i = 0; i < xCoords.Count - 1; i++)
                {
                    Point3d pt1 = new Point3d(xCoords[i], maxY + extension + (bubbleRad * 2), 0);
                    Point3d pt2 = new Point3d(xCoords[i + 1], maxY + extension + (bubbleRad * 2), 0);
                    Point3d dimLinePt = new Point3d(xCoords[i], maxY + extension + (bubbleRad * 2) + dimOffset, 0);

                    RotatedDimension rotDim = new RotatedDimension(0.0, pt1, pt2, dimLinePt, "<>", db.Dimstyle)
                    {
                        Layer = "S-GRID-DIMS"
                    };
                    btr.AppendEntity(rotDim);
                    tr.AddNewlyCreatedDBObject(rotDim, true);
                }

                string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
                for (int j = 0; j < yCoords.Count; j++)
                {
                    double y = yCoords[j];
                    string tag = j < 26 ? alphabet[j].ToString() : "A" + (j - 25);

                    Point3d p1 = new Point3d(minX - extension, y, 0);
                    Point3d p2 = new Point3d(maxX + extension, y, 0);

                    Line line = new Line(p1, p2) { Layer = "S-GRID-LINE" };
                    AttachMetadata(line, "HORIZONTAL", tag);
                    btr.AppendEntity(line);
                    tr.AddNewlyCreatedDBObject(line, true);

                    CreateBubble(btr, tr, new Point3d(minX - extension - bubbleRad, y, 0), bubbleRad, tag, textHt);
                    CreateBubble(btr, tr, new Point3d(maxX + extension + bubbleRad, y, 0), bubbleRad, tag, textHt);
                }

                for (int j = 0; j < yCoords.Count - 1; j++)
                {
                    Point3d pt1 = new Point3d(minX - extension - (bubbleRad * 2), yCoords[j], 0);
                    Point3d pt2 = new Point3d(minX - extension - (bubbleRad * 2), yCoords[j + 1], 0);
                    Point3d dimLinePt = new Point3d(minX - extension - (bubbleRad * 2) - dimOffset, yCoords[j], 0);

                    RotatedDimension rotDim = new RotatedDimension(Math.PI / 2.0, pt1, pt2, dimLinePt, "<>", db.Dimstyle)
                    {
                        Layer = "S-GRID-DIMS"
                    };
                    btr.AppendEntity(rotDim);
                    tr.AddNewlyCreatedDBObject(rotDim, true);
                }

                tr.Commit();
                ed.WriteMessage($"\nGenerated: {xCoords.Count} X-Grids, {yCoords.Count} Y-Grids with linear dimensions.");
            }
        }

        [CommandMethod("EDITGRID")]
        public void EditGrid()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
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

        private List<Point3d> ExtractCenters(Transaction tr, SelectionSet ss)
        {
            List<Point3d> centers = new List<Point3d>();
            foreach (SelectedObject so in ss)
            {
                Entity ent = tr.GetObject(so.ObjectId, OpenMode.ForRead) as Entity;
                if (ent == null) continue;

                if (ent is Circle circle)
                {
                    centers.Add(circle.Center);
                }
                else if (ent.Bounds.HasValue)
                {
                    Point3d min = ent.Bounds.Value.MinPoint;
                    Point3d max = ent.Bounds.Value.MaxPoint;
                    centers.Add(new Point3d((min.X + max.X) / 2.0, (min.Y + max.Y) / 2.0, 0));
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

        private void EnsureLayer(Transaction tr, Database db, string name, short colorIdx)
        {
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!lt.Has(name))
            {
                lt.UpgradeOpen();
                LayerTableRecord ltr = new LayerTableRecord
                {
                    Name = name,
                    Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, colorIdx)
                };
                lt.Add(ltr);
                tr.AddNewlyCreatedDBObject(ltr, true);
            }
        }
    }
}
