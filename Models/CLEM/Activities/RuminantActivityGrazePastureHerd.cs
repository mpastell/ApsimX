﻿using Models.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Models.CLEM.Resources;
using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;
using Models.CLEM.Groupings;
using Models.Core.Attributes;
using System.IO;

namespace Models.CLEM.Activities
{
    /// <summary>Ruminant grazing activity</summary>
    /// <summary>Specific version where pasture and breed is specified</summary>
    /// <summary>This activity determines how a ruminant breed will graze on a particular pasture (GrazeFoodSotreType)</summary>
    [Serializable]
    [ViewName("UserInterface.Views.PropertyView")]
    [PresenterName("UserInterface.Presenters.PropertyPresenter")]
    [ValidParent(ParentType = typeof(CLEMActivityBase))]
    [ValidParent(ParentType = typeof(ActivitiesHolder))]
    [ValidParent(ParentType = typeof(ActivityFolder))]
    [Description("This activity performs grazing of a specified herd and pasture (paddock) in the simulation.")]
    [Version(1, 0, 1, "")]
    [HelpUri(@"Content/Features/Activities/Ruminant/RuminantGraze.htm")]
    class RuminantActivityGrazePastureHerd : CLEMRuminantActivityBase
    {
        /// <summary>
        /// Link to clock
        /// Public so children can be dynamically created after links defined
        /// </summary>
        [Link]
        public Clock Clock = null;

        /// <summary>
        /// Number of hours grazed
        /// Based on 8 hour grazing days
        /// Could be modified to account for rain/heat walking to water etc.
        /// </summary>
        [Description("Number of hours grazed (based on 8 hr grazing day)")]
        [Required, Range(0, 8, ErrorMessage = "Value based on maximum 8 hour grazing day"), GreaterThanValue(0)]
        public double HoursGrazed { get; set; }

        /// <summary>
        /// Paddock or pasture to graze
        /// </summary>
        [Description("GrazeFoodStore/pasture to graze")]
        [Required(AllowEmptyStrings = false, ErrorMessage = "Graze Food Store/pasture required")]
        [Core.Display(Type = DisplayType.DropDown, Values = "GetResourcesAvailableByName", ValuesArgs = new object[] { new object[] { typeof(GrazeFoodStore) } })]
        public string GrazeFoodStoreTypeName { get; set; }

        /// <summary>
        /// paddock or pasture to graze
        /// </summary>
        [JsonIgnore]
        public GrazeFoodStoreType GrazeFoodStoreModel { get; set; }

        /// <summary>
        /// Ruminant group to graze
        /// </summary>
        [Description("Ruminant type to graze")]
        [Required(AllowEmptyStrings = false, ErrorMessage = "Ruminant Type required")]
        [Core.Display(Type = DisplayType.DropDown, Values = "GetResourcesAvailableByName", ValuesArgs = new object[] { new object[] { typeof(RuminantHerd) } })]
        public string RuminantTypeName { get; set; }

        /// <summary>
        /// Ruminant group to graze
        /// </summary>
        [JsonIgnore]
        public RuminantType RuminantTypeModel { get; set; }

        /// <summary>
        /// The proportion of required graze that is available determined from parent activity arbitration
        /// </summary>
        [JsonIgnore]
        public double GrazingCompetitionLimiter { get; set; }

        /// <summary>
        /// The biomass of pasture per hectare at start of allocation
        /// </summary>
        [JsonIgnore]
        public double BiomassPerHectare { get; set; }

        /// <summary>
        /// Potential intake limiter based on pasture quality
        /// </summary>
        [JsonIgnore]
        public double PotentialIntakePastureQualityLimiter { get; set; }

        /// <summary>
        /// Dry matter digestibility of pasture consumed (%)
        /// </summary>
        [JsonIgnore]
        public double DMD { get; set; }

        /// <summary>
        /// Nitrogen of pasture consumed (%)
        /// </summary>
        [JsonIgnore]
        public double N { get; set; }

        /// <summary>
        /// Proportion of intake that can be taken from each pool
        /// </summary>
        [JsonIgnore]
        public List<GrazeBreedPoolLimit> PoolFeedLimits { get; set; }

        /// <summary>
        /// Constructor
        /// </summary>
        public RuminantActivityGrazePastureHerd()
        {
            TransactionCategory = "Livestock.Grazing";
        }

        /// <summary>An event handler to allow us to initialise ourselves.</summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        [EventSubscribe("CLEMInitialiseActivity")]
        private void OnCLEMInitialiseActivity(object sender, EventArgs e)
        {
            // This method will only fire if the user has added this activity to the UI
            // Otherwise all details will be provided from GrazeAll or GrazePaddock code [CLEMInitialiseActivity]

            this.InitialiseHerd(true, false);

            // if no settings have been provided from parent set limiter to 1.0. i.e. no limitation
            if (GrazingCompetitionLimiter == 0)
            {
                GrazingCompetitionLimiter = 1.0;
            }

            GrazeFoodStoreModel = Resources.FindResourceType<GrazeFoodStore, GrazeFoodStoreType>(this, GrazeFoodStoreTypeName, OnMissingResourceActionTypes.ReportErrorAndStop, OnMissingResourceActionTypes.ReportErrorAndStop);

            RuminantTypeModel = Resources.FindResourceType<RuminantHerd, RuminantType>(this, RuminantTypeName, OnMissingResourceActionTypes.ReportErrorAndStop, OnMissingResourceActionTypes.ReportErrorAndStop);
        }

        /// <summary>An event handler to allow us to clear requests at start of month.</summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        [EventSubscribe("StartOfMonth")]
        private void OnStartOfMonth(object sender, EventArgs e)
        {
            ResourceRequestList = null;
            this.PoolFeedLimits = null;
        }

        /// <inheritdoc/>
        public override void GetResourcesRequiredForActivity()
        {
            // if there is no ResourceRequestList (i.e. not created from parent pasture)
            if (ResourceRequestList == null)
            {
                PotentialIntakePastureQualityLimiter = CalculatePotentialIntakePastureQualityLimiter();
                GetResourcesNeededForActivity();
            }
            // The DoActivity has all the code to feed animals.
            DoActivity();
        }

        /// <summary>
        /// Calculate the potential intake limiter based on pasture quality.
        /// </summary>
        /// <returns>Limiter as proportion</returns>
        public double CalculatePotentialIntakePastureQualityLimiter()
        {
            // determine pasture quality from all pools (DMD) at start of grazing
            double pastureDMD = GrazeFoodStoreModel.DMD;
            // Reduce potential intake based on pasture quality for the proportion consumed (zero legume).
            // TODO: check that this doesn't need to be performed for each breed based on how pasture taken
            // this will still occur when grazing on improved, irrigated or crops. 
            // CLEM does not allow grazing on two pastures in the month, whereas NABSA allowed irrigated pasture and supplemented with native for remainder needed.
            if ((0.8 - GrazeFoodStoreModel.IntakeTropicalQualityCoefficient - pastureDMD / 100) >= 0)
            {
                return 1 - GrazeFoodStoreModel.IntakeQualityCoefficient * (0.8 - GrazeFoodStoreModel.IntakeTropicalQualityCoefficient - pastureDMD / 100);
            }
            else
            {
                return 1;
            }
        }

        /// <inheritdoc/>
        public override List<ResourceRequest> GetResourcesNeededForActivity()
        {
            // check if resource request list has been calculated from a parent call
            if (ResourceRequestList == null)
            {
                ResourceRequestList = new List<ResourceRequest>();
                IEnumerable<Ruminant> herd = this.CurrentHerd(false).Where(a => a.Location == this.GrazeFoodStoreModel.Name && a.HerdName == this.RuminantTypeModel.Name);
                if (herd.Any())
                {
                    double amount = 0;
                    double indAmount = 0;
                    // get list of all Ruminants of specified breed in this paddock
                    foreach (Ruminant ind in herd)
                    {
                        if (!ind.Weaned)
                        {
                            // treat sucklings separate
                            // they eat what was previously assigned in RuminantGrow minus what's been fed
                            amount += ind.PotentialIntake - ind.MilkIntake - ind.Intake;
                        }
                        else
                        {
                            // Reduce potential intake (monthly) based on pasture quality for the proportion consumed calculated in GrazePasture.
                            // calculate intake from potential modified by pasture availability and hours grazed
                            indAmount = ind.PotentialIntake * PotentialIntakePastureQualityLimiter * (1 - Math.Exp(-ind.BreedParams.IntakeCoefficientBiomass * this.GrazeFoodStoreModel.TonnesPerHectareStartOfTimeStep * 1000)) * (HoursGrazed / 8);

                            // assumes animals will stop eating at potential intake if they have been feed before grazing.
                            // hours grazed is not adjusted for this reduced feeding. Used to be 1.2 * Potential intake
                            indAmount = Math.Min(ind.PotentialIntake - ind.Intake, indAmount);
                            amount += indAmount;
                        }
                    }
                    if (amount > 0)
                    {
                        ResourceRequestList.Add(new ResourceRequest()
                        {
                            AllowTransmutation = true,
                            Required = amount,
                            ResourceType = typeof(GrazeFoodStore),
                            ResourceTypeName = this.GrazeFoodStoreModel.Name,
                            ActivityModel = this,
                            AdditionalDetails = this
                        }
                        );
                    }

                    if ( GrazeFoodStoreTypeName != null && GrazeFoodStoreModel != null)
                    {
                        // Stand alone model has not been set by parent RuminantActivityGrazePasture
                        SetupPoolsAndLimits(1.0);
                    }
                }
            }
            return ResourceRequestList;
        }

        /// <summary>
        /// Method to set up pools from currently available graze pools and limit based upon green content herd limit parameters
        /// </summary>
        /// <param name="limit">The competition limit defined from GrazePasture parent</param>
        public void SetupPoolsAndLimits(double limit)
        {
            this.GrazingCompetitionLimiter = limit;
            // store kg/ha available for consumption calculation
            this.BiomassPerHectare = GrazeFoodStoreModel.KilogramsPerHa;

            // calculate breed feed limits
            if (this.PoolFeedLimits == null)
            {
                this.PoolFeedLimits = new List<GrazeBreedPoolLimit>();
            }
            else
            {
                this.PoolFeedLimits.Clear();
            }

            foreach (var pool in GrazeFoodStoreModel.Pools)
            {
                this.PoolFeedLimits.Add(new GrazeBreedPoolLimit() { Limit = 1.0, Pool = pool });
            }

            // if Jan-March then use first three months otherwise use 2
            int greenage = (Clock.Today.Month <= 3) ? 2 : 1;

            double green = GrazeFoodStoreModel.Pools.Where(a => (a.Age <= greenage)).Sum(b => b.Amount);
            double propgreen = green / GrazeFoodStoreModel.Amount;
            
            // All values are now proportions.
            // Convert to percentage before calculation
            
            double greenlimit = (this.RuminantTypeModel.GreenDietMax*100) * (1 - Math.Exp(-this.RuminantTypeModel.GreenDietCoefficient * ((propgreen*100) - (this.RuminantTypeModel.GreenDietZero*100))));
            greenlimit = Math.Max(0.0, greenlimit);
            if (propgreen > 0.9)
            {
                greenlimit = 100;
            }

            foreach (var pool in this.PoolFeedLimits.Where(a => a.Pool.Age <= greenage))
            {
                pool.Limit = greenlimit / 100.0;
            }

            // order feedpools by age so that diet is taken from youngest greenest first
            this.PoolFeedLimits = this.PoolFeedLimits.OrderBy(a => a.Pool.Age).ToList();
        }

        /// <inheritdoc/>
        public override void DoActivity()
        {
            //Go through amount received and put it into the animals intake with quality measures.
            if (ResourceRequestList != null)
            {
                IEnumerable<Ruminant> herd = this.CurrentHerd(false).Where(a => a.Location == this.GrazeFoodStoreModel.Name && a.HerdName == this.RuminantTypeModel.Name);
                if (herd.Any())
                {
                    // Get total amount
                    // assumes animals will stop eating at potential intake if they have been feed before grazing.
                    // hours grazed is not adjusted for this reduced feeding. Used to be 1.2 * Potential
                    double totalDesired = 0;
                    double totalEaten = 0;
                    // sucklings
                    totalDesired = herd.Where(a => !a.Weaned).Sum(a => a.PotentialIntake - a.Intake);
                    totalEaten = herd.Where(a => !a.Weaned).Sum(a => a.PotentialIntake - a.Intake);
                    // weaned
                    totalDesired += herd.Where(a => a.Weaned).Sum(a => Math.Min(a.PotentialIntake - a.Intake, a.PotentialIntake * PotentialIntakePastureQualityLimiter * (HoursGrazed / 8)));
                    totalEaten += herd.Where(a => a.Weaned).Sum(a => Math.Min(a.PotentialIntake - a.Intake, a.PotentialIntake * PotentialIntakePastureQualityLimiter * (1 - Math.Exp(-a.BreedParams.IntakeCoefficientBiomass * this.GrazeFoodStoreModel.TonnesPerHectareStartOfTimeStep * 1000)) * (HoursGrazed / 8)));

                    totalEaten *= GrazingCompetitionLimiter;

                    // take resource
                    if (totalEaten > 0)
                    {
                        ResourceRequest request = new ResourceRequest()
                        {
                            ActivityModel = this,
                            AdditionalDetails = this,
                            Category = TransactionCategory,
                            RelatesToResource = RuminantTypeModel.NameWithParent,
                            Required = totalEaten,
                            Resource = GrazeFoodStoreModel as IResourceType
                        };
                        GrazeFoodStoreModel.Remove(request);

                        FoodResourcePacket food = new FoodResourcePacket()
                        {
                            DMD = ((RuminantActivityGrazePastureHerd)request.AdditionalDetails).DMD,
                            PercentN = ((RuminantActivityGrazePastureHerd)request.AdditionalDetails).N
                        };

                        double shortfall = request.Provided / request.Required;

                        // allocate to individuals
                        foreach (Ruminant ind in herd)
                        {
                            double eaten;
                            if (ind.Weaned)
                                eaten = ind.PotentialIntake * PotentialIntakePastureQualityLimiter * (HoursGrazed / 8);
                            else
                                eaten = ind.PotentialIntake - ind.Intake;

                            food.Amount = eaten * GrazingCompetitionLimiter * shortfall;
                            ind.AddIntake(food);
                        }
                        Status = ActivityStatus.Success;

                        // if insufficent provided or no pasture (nothing eaten) use totalNeededifPasturePresent
                        if (GrazingCompetitionLimiter < 1)
                        {
                            request.Available = request.Provided; // display all that was given
                            request.Required = totalDesired;
                            request.ResourceType = typeof(GrazeFoodStore);
                            request.ResourceTypeName = GrazeFoodStoreModel.Name;
                            ResourceRequestEventArgs rre = new ResourceRequestEventArgs() { Request = request };
                            OnShortfallOccurred(rre);

                            if (this.OnPartialResourcesAvailableAction == OnPartialResourcesAvailableActionTypes.ReportErrorAndStop)
                                throw new ApsimXException(this, "Insufficient pasture available for grazing in paddock (" + GrazeFoodStoreModel.Name + ") in " + Clock.Today.Month.ToString() + "\\" + Clock.Today.Year.ToString());

                            this.Status = ActivityStatus.Partial;
                        }
                    }
                    else
                        Status = ActivityStatus.NotNeeded;
                }
            }
            else
            {
                if (Status != ActivityStatus.Partial && Status != ActivityStatus.Critical)
                    Status = ActivityStatus.NotNeeded;
            }
        }

        /// <inheritdoc/>
        public override GetDaysLabourRequiredReturnArgs GetDaysLabourRequired(LabourRequirement requirement)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc/>
        public override void AdjustResourcesNeededForActivity()
        {
            return;
        }

        /// <inheritdoc/>
        public override List<ResourceRequest> GetResourcesNeededForinitialisation()
        {
            return null;
        }

        /// <inheritdoc/>
        public override event EventHandler ResourceShortfallOccurred;

        /// <inheritdoc/>
        protected override void OnShortfallOccurred(EventArgs e)
        {
            ResourceShortfallOccurred?.Invoke(this, e);
        }

        /// <inheritdoc/>
        public override event EventHandler ActivityPerformed;

        /// <inheritdoc/>
        protected override void OnActivityPerformed(EventArgs e)
        {
            ActivityPerformed?.Invoke(this, e);
        }

        #region descriptive summary

        /// <inheritdoc/>
        public override string ModelSummary(bool formatForParentControl)
        {
            using (StringWriter htmlWriter = new StringWriter())
            {
                htmlWriter.Write("\r\n<div class=\"activityentry\">All individuals of ");
                if (RuminantTypeName == null || RuminantTypeName == "")
                {
                    htmlWriter.Write("<span class=\"errorlink\">[HERD NOT SET]</span>");
                }
                else
                {
                    htmlWriter.Write("<span class=\"resourcelink\">" + RuminantTypeName + "</span>");
                }
                htmlWriter.Write(" in ");
                if (GrazeFoodStoreTypeName == null || GrazeFoodStoreTypeName == "")
                {
                    htmlWriter.Write("<span class=\"errorlink\">[PASTURE NOT SET]</span>");
                }
                else
                {
                    htmlWriter.Write("<span class=\"resourcelink\">" + GrazeFoodStoreTypeName + "</span>");
                }
                htmlWriter.Write(" will graze for ");
                htmlWriter.Write("\r\n<div class=\"activityentry\">All individuals in managed pastures will graze for ");
                if (HoursGrazed <= 0)
                {
                    htmlWriter.Write("<span class=\"errorlink\">" + HoursGrazed.ToString("0.#") + "</span> hours of ");
                }
                else
                {
                    htmlWriter.Write(((HoursGrazed == 8) ? "" : "<span class=\"setvalue\">" + HoursGrazed.ToString("0.#") + "</span> hours of "));
                }

                htmlWriter.Write("the maximum 8 hours each day</span>");
                htmlWriter.Write("</div>");
                return htmlWriter.ToString(); 
            }
        } 
        #endregion
    }
}
