using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace MetaMap
{
    public class MetaTemplateCMP : GH_Component
    {
        private List<string> folderList = new List<string>();
        private List<List<string>> filesList = new List<List<string>>();

        public MetaTemplateCMP()
          : base("MetaTEMPLATE", "MetaTEMPLATE",
              "MetaMAP template files for quick starting",
              "MetaMAP", "Templates")
        {
        }

        public override Guid ComponentGuid => new Guid("23456789-2345-2345-2345-234567890123");

        protected override Bitmap Icon => MetaResources.GetIcon("MetaTemplate.png");

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("Directory", "Dir", "Additional folder path to import MetaMAP templates.", GH_ParamAccess.list);
            pManager[0].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Templates", "T", "MetaMAP templates found in folders.", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            this.folderList = new List<string>();
            this.filesList = new List<List<string>>();

            // Default Templates folder next to the plugin (works for manual installs and Yak packages).
            var dirs = new List<string>();
            try
            {
                string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(pluginDir))
                    dirs.Add(Path.Combine(pluginDir, "Templates"));
            }
            catch
            {
                // Location can be empty for dynamically loaded assemblies; user folders still work.
            }

            // Add any additional directories from input
            var additionalDirs = new List<string>();
            DA.GetDataList(0, additionalDirs);
            dirs.AddRange(additionalDirs);

            // Filter to only existing directories
            dirs = dirs.Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d)).Distinct().ToList();

            // Scan each directory for template files
            foreach (var dir in dirs)
            {
                List<string> fs;
                try
                {
                    fs = Directory.GetFiles(dir, "*.gh*", SearchOption.AllDirectories)
                        .Where(f => f.EndsWith(".gh", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ghx", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                catch (Exception ex)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Cannot read {dir}: {ex.Message}");
                    continue;
                }

                if (fs.Any())
                {
                    this.folderList.Add(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    this.filesList.Add(fs);
                }
            }

            if (this.filesList.Count == 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "No templates found. Add a folder path to the Directory input.");

            DA.SetDataList(0, this.filesList.SelectMany(f => f));
        }

        private Size GetMoveVector(PointF FromLocation)
        {
            var moveX = this.Attributes.Bounds.Left - 80 - FromLocation.X;
            var moveY = this.Attributes.Bounds.Y + 180 - FromLocation.Y;
            var loc = new Point(Convert.ToInt32(moveX), Convert.ToInt32(moveY));

            return new Size(loc);
        }

        private void CreateTemplateFromFile(string FilePath)
        {
            // Note: the canvas is deliberately NOT required to be focused. On macOS the context
            // menu takes keyboard focus away from the canvas, which used to make this a silent no-op.
            if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath))
            {
                Rhino.UI.Dialogs.ShowMessage($"Template not found:{Environment.NewLine}{FilePath}", "MetaMAP");
                return;
            }

            var docCurrent = OnPingDocument() ?? Grasshopper.Instances.ActiveCanvas?.Document;
            if (docCurrent == null)
            {
                Rhino.UI.Dialogs.ShowMessage("No active Grasshopper document.", "MetaMAP");
                return;
            }

            try
            {
                var io = new GH_DocumentIO();
                if (!io.Open(FilePath) || io.Document == null)
                {
                    Rhino.UI.Dialogs.ShowMessage($"Failed to load template:{Environment.NewLine}{FilePath}", "MetaMAP");
                    return;
                }

                var docTemp = io.Document;

                // Generate new IDs to avoid conflicts with objects already on the canvas
                docTemp.SelectAll();
                docTemp.MutateAllIds();

                // Move template to position near this component
                var box = docTemp.BoundingBox(false);
                var vec = GetMoveVector(box.Location);
                docTemp.TranslateObjects(vec, true);
                docTemp.ExpireSolution();

                // Merge template into current document and leave the new objects selected
                docCurrent.DeselectAll();
                docCurrent.MergeDocument(docTemp);
                docTemp.SelectAll();

                var canvas = Grasshopper.Instances.ActiveCanvas;
                if (canvas != null)
                {
                    canvas.Refresh();
                }
            }
            catch (Exception ex)
            {
                Rhino.UI.Dialogs.ShowMessage($"Failed to insert template:{Environment.NewLine}{ex.Message}", "MetaMAP");
            }
        }

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            var newMenu = menu;
            newMenu.Items.Clear();

            if (this.filesList.Count == 0)
            {
                Menu_AppendItem(menu, "No templates found", null, false);
                return;
            }

            var count = 0;
            foreach (var filesPerFolder in this.filesList)
            {
                var menuItem = AddFromFolder(this.folderList[count], filesPerFolder);
                menu.Items.Add(menuItem);
                count++;
            }
        }

        private ToolStripMenuItem AddFromFolder(string rootFolder, List<string> filesPerFolder)
        {
            var folderName = new DirectoryInfo(rootFolder).Name;
            var t = new ToolStripMenuItem(folderName);

            foreach (var item in filesPerFolder)
            {
                var p = Path.GetDirectoryName(item) ?? string.Empty;
                var name = Path.GetFileNameWithoutExtension(item);
                var showName = name;
                if (p.Length > rootFolder.Length && p.StartsWith(rootFolder, StringComparison.OrdinalIgnoreCase))
                {
                    var sub = p.Substring(rootFolder.Length).Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (sub.Length > 0)
                        showName = sub.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + name;
                }

                string filePath = item;
                EventHandler ev = (object sender, EventArgs e) =>
                {
                    CreateTemplateFromFile(filePath);
                    this.ExpireSolution(true);
                };

                Menu_AppendItem(t.DropDown, showName, ev, null, item);
            }

            return t;
        }

        public override void CreateAttributes()
        {
            m_attributes = new MetaTemplateAttributes(this);
        }
    }

    public class MetaTemplateAttributes : GH_ComponentAttributes
    {
        public MetaTemplateAttributes(GH_Component owner) : base(owner)
        {
        }

        protected override void Layout()
        {
            base.Layout();
            
            // Add extra space below for the message
            var bounds = Bounds;
            bounds.Height += 20;
            Bounds = bounds;
        }

        protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
        {
            base.Render(canvas, graphics, channel);

            if (channel == GH_CanvasChannel.Objects)
            {
                // Draw "Right click" message below the component
                var palette = GH_Palette.Black;
                var capsule = GH_Capsule.CreateCapsule(new RectangleF(Bounds.X, Bounds.Bottom - 20, Bounds.Width, 18), palette);
                capsule.Render(graphics, Selected, Owner.Locked, false);
                capsule.Dispose();

                // Draw text
                var textBounds = new RectangleF(Bounds.X, Bounds.Bottom - 20, Bounds.Width, 18);
                var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                
                graphics.DrawString("Right click", GH_FontServer.Small, Brushes.White, textBounds, format);
            }
        }
    }
}
