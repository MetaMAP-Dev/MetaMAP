using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Rhino;
using Rhino.Geometry;
using Grasshopper;
using Grasshopper.Kernel;

namespace MetaMAP
{
    public class TabProperties : GH_AssemblyPriority
    {
        public override GH_LoadingInstruction PriorityLoad()
        {
            var server = Grasshopper.Instances.ComponentServer;

            // Register the MetaMAP category
            // The category will be created when the first component is loaded
            // server.AddCategoryIcon("MetaMAP", icon);

            // Privacy-first usage analytics (ported from Eddy3D) — tracks which ribbon tab is
            // used, once per machine per day. See MetaMap.Analytics.Analytics for the opt-out.
            MetaMap.Analytics.TabUsage.Install();

            return GH_LoadingInstruction.Proceed;
        }
    }
}
