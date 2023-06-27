using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Models.Climate;
using Models.Core;
using Models.PMF.Organs;
using Models.PostSimulationTools;
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

        /// <summary>Ensemble size</summary>
        public Int64 N { get;}

        /// <summary>Maximum number of cores to use</summary>
        public int NCores { get => parallelOptions.MaxDegreeOfParallelism;
                            set => parallelOptions.MaxDegreeOfParallelism = value;
        }

        /// <summary>Simulation current date</summary>
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
        private ParallelOptions parallelOptions;

        /// <summary>Initialize the model ensemble</summary>
        public ModelEnsemble(IModel imodel, Int64 N, int Ncores = -1)
        {
            if (Ncores == -1)
                Ncores = Environment.ProcessorCount;
            parallelOptions = new ParallelOptions();
            parallelOptions.MaxDegreeOfParallelism = Ncores;
            this.N = N;

            Model model = (Model)imodel;
            var storage = model.FindChild<DataStore>();
            storage.Enabled = false;
            var reports = model.FindAllDescendants<Report>();

            foreach (var report in reports)
            {
                model.Children.Remove(report);
            }

            var summaries = model.FindAllDescendants<Summary>();
            foreach (var summary in summaries)
            {
                summary.Verbosity = MessageType.Error;
            }

            ConcurrentBag<Model> ModelBag = new ConcurrentBag<Model>();

            Parallel.For(0, N, parallelOptions, (i, loopState) =>
            {
                var cmodel = model.Clone();
                ModelBag.Add(cmodel);
            });

            Models = ModelBag.ToList();
            Simulations = Models.Select(m => m.FindDescendant<Simulation>()).ToList();
            Clocks = Simulations.Select(s => s.FindDescendant<TwinClock>()).ToList();
        }

        /// <summary>Prepare simulations</summary>
        public void Prepare()
        {
            Parallel.ForEach(Simulations, parallelOptions, s => s.Prepare());
        }

        /// <summary>Commence simulations</summary>
        public void Commence()
        {
            cts = new CancellationTokenSource();
            Parallel.ForEach(Clocks, parallelOptions, c => c.Commence(cts));
        }

        /// <summary>Progress by one day</summary>
        public void Step()
        {
            //Clocks.ForEach(c => c.Step());
            Parallel.ForEach(Clocks, parallelOptions, c => c.Step());
        }

        /// <summary>Progress by one day</summary>
        public void Done()
        {
            Parallel.ForEach(Clocks, parallelOptions, c => c.Done());
            Parallel.ForEach(Simulations, parallelOptions, sim => sim.Cleanup());
            //Clocks.ForEach(c => c.Done());
            //Simulations.ForEach(sim => sim.Cleanup());
            cts.Cancel();
        }
    }
}
