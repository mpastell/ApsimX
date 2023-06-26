using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Models.Core;
using Models.Storage;
using Models.Core.ApsimFile;

namespace Models.TwinYields
{
    /// <summary>Ensemble of models</summary>
    public class SimulationEnsemble
    {
        /// <summary>List of models</summary>
        public Model Model { get; set; }
        /// <summary>All clocks</summary>
        public List<TwinClock> Clocks { get; }
        /// <summary>List of simulations</summary>
        public List<Simulation> Simulations { get; }
        /// <summary>Clock today</summary>
        public DateTime Today
        {
            get => Clocks[0].Today;
        }
        /// <summary>End date</summary>
        public DateTime EndDate
        {
            get { return Clocks[0].EndDate; }
        }

        private CancellationTokenSource cts;

        /// <summary>Initialize the model ensemble</summary>
        public SimulationEnsemble(IModel imodel, Int64 N)
        {
            Model = (Model)imodel;
            var storage = Model.FindChild<DataStore>();
            storage.Enabled = false;
            var reports = Model.FindAllDescendants<Report>();

            foreach (var report in reports)
            {
                Model.Children.Remove(report);
            }

            Simulations = new List<Simulation>();
            Clocks = new List<TwinClock>();

            var orig_sim = Model.FindDescendant<Simulation>();

            for (int i = 0; i < N; i++)
            {
                orig_sim.Name = $"sim{i}";
                var sim = orig_sim.Clone();
                var clock = sim.FindDescendant<TwinClock>();
                Model.Children.Add(sim);
            }
            Model.Children.Remove(orig_sim);
            var json = FileFormat.WriteToString(Model);
            var outfile ="CSEnsemble.apsimx";
            File.WriteAllText(outfile, json);
            IModel sims = FileFormat.ReadFromFile<Simulations>(outfile, e => throw e, false).NewModel;
            Model = (Model)sims;
            Simulations = Model.FindAllDescendants<Simulation>().ToList();
            Clocks = Model.FindAllDescendants<TwinClock>().ToList();
        }

        /// <summary>Prepare simulations</summary>
        public void Prepare()
        {
            Simulations.ForEach(s => s.Prepare());
        }

        /// <summary>Commence simulations</summary>
        public void Commence()
        {
            cts = new CancellationTokenSource();
            Clocks.ForEach(c => c.Commence(cts));
        }

        /// <summary>Progress by one day</summary>
        public void Step()
        {
            Clocks.ForEach(c => c.Step());
        }

        /// <summary>Progress by one day</summary>
        public void Done()
        {
            Clocks.ForEach(c => c.Done());
            Simulations.ForEach(sim => sim.Cleanup());
            cts.Cancel();
        }
    }
}
