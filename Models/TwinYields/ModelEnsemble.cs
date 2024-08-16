using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Models.Climate;
using Models.Core;
using Models.PMF.Organs;
using Models.PostSimulationTools;
using Models.Storage;
using Models.Soils.NutrientPatching;
using Models.PMF;
using Models.Utilities;
using Models.CLEM.Activities;

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
        private Model model;

        /// <summary>Initialize the model ensemble</summary>
        public ModelEnsemble(IModel imodel, Int64 N, int Ncores = -1)
        {
            if (Ncores == -1)
                Ncores = Environment.ProcessorCount;
            parallelOptions = new ParallelOptions();
            parallelOptions.MaxDegreeOfParallelism = Ncores;
            this.N = N;

            model = (Model)imodel;
            Ensemblify(model);

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

        //Remove extra models and replace Clock with TwinClock
        private void Ensemblify(Model model)
        {
            var storage = model.FindChild<DataStore>();
            storage.Enabled = false;

            //Remove report objects
            var reports = model.FindAllDescendants<Report>().ToList();
            reports.ForEach(r => r.Parent.Children.Remove(r));

            //Reduce summary verbosity
            var summaries = model.FindAllDescendants<Summary>().ToList();
            summaries.ForEach(summary => summary.Verbosity = MessageType.Error);

            //Replace clocks with TwinClocks
            var clock = model.FindDescendant<Clock>();
            if (clock != null)
            {
                TwinClock twinClock = new TwinClock();
                twinClock.StartDate = clock.StartDate;
                twinClock.EndDate = clock.EndDate;
                var cp = clock.Parent;
                cp.Children.Remove(clock);
                cp.Children.Add(twinClock);

                //Fix reference in manager scripts
                var managers = model.FindAllDescendants<Manager>();
                foreach (var manager in managers)
                {
                    //manager.Code = manager.Code.Replace("Clock", "IClock");
                    manager.Code = Regex.Replace(manager.Code, "[^I]Clock", " IClock");
                }
            }
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
