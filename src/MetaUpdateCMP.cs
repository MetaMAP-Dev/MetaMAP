using Grasshopper.Kernel;
using System;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace MetaMap
{
    public class MetaUpdateCMP : GH_Component
    {
        private string _statusMessage = "Set Update to true to check Rhino Package Manager.";
        private bool _isChecking;
        private bool _wasRequested;

        public MetaUpdateCMP()
          : base("MetaUPDATE", "MetaUPDATE",
              "Check Yak for MetaMAP updates and install through Rhino Package Manager",
              "MetaMAP", "Templates")
        {
        }

        public override Guid ComponentGuid => new Guid("12345678-1234-1234-1234-123456789012");

        protected override Bitmap Icon => MetaResources.GetIcon("MetaUpdate.png");

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            // Keep the existing input and output order for saved definitions.
            pManager.AddBooleanParameter("Update", "Upd", "Set to true to check Yak for a new version. Install updates through Rhino Package Manager.", GH_ParamAccess.item, false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Status", "S", "Yak update check and Package Manager instructions", GH_ParamAccess.item);
            pManager.AddTextParameter("Your Version", "V", "Currently loaded version", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            bool requested = false;
            if (!DA.GetData(0, ref requested)) return;

            string currentVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            bool startCheck = requested && !_wasRequested && !_isChecking;
            _wasRequested = requested;

            if (startCheck)
            {
                _isChecking = true;
                _statusMessage = "Checking Rhino Package Manager (Yak)...";
                _ = Task.Run(() => CheckForUpdate(currentVersion));
            }

            // Retain the result when a momentary button returns to false.
            DA.SetData(0, _statusMessage);
            DA.SetData(1, currentVersion);
        }

        private async Task CheckForUpdate(string currentVersion)
        {
            string status;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                status = await YakUpdateChecker.CheckAsync(MetaHttp.Client, currentVersion, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                status = "Yak update check timed out. " + YakUpdateChecker.InstallInstructions;
            }
            catch (Exception ex)
            {
                status = $"Could not check Yak: {ex.Message}. " + YakUpdateChecker.InstallInstructions;
            }

            // All component state and solution changes happen on Rhino's UI thread.
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                _statusMessage = status;
                _isChecking = false;
                if (OnPingDocument() != null)
                    ExpireSolution(true);
            }));
        }
    }
}
