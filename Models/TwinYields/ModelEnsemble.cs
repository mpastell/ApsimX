using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Models.Core;
using Models.Storage;

namespace Models.TwinYields
{
    /// <summary>Ensemble of models</summary>
    public class ModelEnsemble
    {
        /// <summary>List of models</summary>
        public List<Model> Models { get; set; }
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
        public ModelEnsemble(IModel imodel, Int64 N)
        {
            Model model = (Model)imodel;
            var storage = model.FindChild<DataStore>();
            storage.Enabled = false;
            var reports = model.FindAllDescendants<Report>();

            foreach (var report in reports)
            {
                model.Children.Remove(report);
            }

            Models = new List<Model>();
            Simulations = new List<Simulation>();
            Clocks = new List<TwinClock>();

            for (int i = 0; i < N; i++)
            {
                var cmodel = model.Clone();
                var sim = cmodel.FindDescendant<Simulation>();
                var clock = sim.FindDescendant<TwinClock>();
                Models.Add(cmodel);
                Simulations.Add(sim);
                Clocks.Add(clock);
            }
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
        }
    }
}
