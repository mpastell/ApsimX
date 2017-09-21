﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Models.Factorial;
using Models.PMF.Interfaces;
using Models.Graph;
using System.Data;

namespace Models.Core
{
    /// <summary>
    /// A folder model
    /// </summary>
    [ViewName("UserInterface.Views.FolderView")]
    [PresenterName("UserInterface.Presenters.FolderPresenter")]
    [ScopedModel]
    [Serializable]
    [ValidParent(ParentType = typeof(Simulation))]
    [ValidParent(ParentType = typeof(Zone))]
    [ValidParent(ParentType = typeof(Folder))]
    [ValidParent(ParentType = typeof(Simulations))]
    [ValidParent(ParentType = typeof(Experiment))]
    [ValidParent(ParentType = typeof(IOrgan))]
    public class Folder : Model
    {
        /// <summary>Show page of graphs?</summary>
        public bool ShowPageOfGraphs { get; set; }

        /// <summary>Constructor</summary>
        public Folder()
        {
            ShowPageOfGraphs = true;
        }
        /// <summary>Writes documentation for this function by adding to the list of documentation tags.</summary>
        /// <param name="tags">The list of tags to add to.</param>
        /// <param name="headingLevel">The level (e.g. H2) of the headings.</param>
        /// <param name="indent">The level of indentation 1, 2, 3 etc.</param>
        public override void Document(List<AutoDocumentation.ITag> tags, int headingLevel, int indent)
        {
            if (IncludeInDocumentation)
            {
                // add a heading.
                tags.Add(new AutoDocumentation.Heading(Name, headingLevel));

                if (ShowPageOfGraphs)
                {
                    foreach (Memo memo in Apsim.Children(this, typeof(Memo)))
                        memo.Document(tags, headingLevel, indent);

                    if (Apsim.Children(this, typeof(Experiment)).Count > 0)
                    {
                        // Write Phase Table
                        tags.Add(new AutoDocumentation.Paragraph("**List of experiments.**", indent));
                        DataTable tableData = new DataTable();
                        tableData.Columns.Add("Experiment Name", typeof(string));
                        tableData.Columns.Add("Design (Number of Treatments)", typeof(string));

                        foreach (IModel child in Apsim.Children(this, typeof(Experiment)))
                        {
                            IModel Factors = Apsim.Child(child, typeof(Factors));
                            string Design = "";
                            foreach (IModel factor in Apsim.Children(Factors, typeof(Factor)))
                            {
                                if (Design != "")
                                    Design += " x ";
                                Design += factor.Name;
                            }
                            Design += " (" + (child as Experiment).Names().Length + ")";

                            DataRow row = tableData.NewRow();
                            row[0] = child.Name;
                            row[1] = Design;
                            tableData.Rows.Add(row);
                        }
                        tags.Add(new AutoDocumentation.Table(tableData, indent));

                    }
                    int pageNumber = 1;
                    int i = 0;
                    List<IModel> children = Apsim.Children(this, typeof(Graph.Graph));
                    while (i < children.Count)
                    {
                        GraphPage page = new GraphPage();
                        page.name = Name + pageNumber;
                        for (int j = i; j < i + 6 && j < children.Count; j++)
                            if (children[j].IncludeInDocumentation)
                                page.graphs.Add(children[j] as Graph.Graph);
                        if (page.graphs.Count > 0)
                            tags.Add(page);
                        i += 6;
                        pageNumber++;
                    }

                    // Document everything else other than graphs
                    foreach (IModel model in Children)
                        if (!(model is Graph.Graph) && !(model is Memo))
                            model.Document(tags, headingLevel + 1, indent);
                }
                else
                {
                    foreach (IModel model in Children)
                        model.Document(tags, headingLevel + 1, indent);
                }
            }
        }

    }
}
