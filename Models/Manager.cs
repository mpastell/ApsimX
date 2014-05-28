﻿using System;
using System.Xml;
using System.Reflection;
using System.Xml.Serialization;
using Models.Core;
using System.Xml.Schema;
using System.Runtime.Serialization;
using System.IO;

namespace Models
{
    [Serializable]
    [ViewName("UserInterface.Views.ManagerView")]
    [PresenterName("UserInterface.Presenters.ManagerPresenter")]
    public class Manager : Model
    {
        // ----------------- Privates
        private string _Code;
        private bool HasDeserialised = false;
        private string elementsAsXml = null;

        [NonSerialized] private Model _Script;
        [NonSerialized] private XmlElement[] _elements;

        [NonSerialized] private string CompiledCode = "";

        // ----------------- Parameters (XML serialisation)
        [XmlAnyElement]
        public XmlElement[] elements { get { return _elements; } set { _elements = value; } }

        [XmlElement("Code")]
        public XmlNode CodeCData
        {
            get
            {
                XmlDocument dummy = new XmlDocument();
                return dummy.CreateCDataSection(Code);
            }
            set
            {
                if (value == null)
                {
                    Code = null;
                    return;
                }

                Code = value.Value;
            }
        }

        // ----------------- Outputs
        [XmlIgnore]
        public Model Script { get { return _Script; } set { _Script = value; } }

        [XmlIgnore]
        public string Code
        {
            get
            {
                return _Code;
            }
            set
            {
                _Code = value;
                RebuildScriptModel();
            }
        }

        /// <summary>
        /// The model has been loaded.
        /// </summary>
        public override void OnLoaded()
        {
            HasDeserialised = true;
            if (Script == null)
                RebuildScriptModel();
        }

        /// <summary>
        /// We're about to be serialised. Remove our 'Script' model from the list
        /// of all models so that is isn't serialised. Seems .NET has a problem
        /// with serialising objects that have been compiled dynamically.
        /// </summary>
        public override void OnSerialising(bool xmlSerialisation)
        {
            if (Script != null)
                Models.Remove(Script);
        }

        /// <summary>
        /// Serialisation has completed. Read our 'Script' model if necessary.
        /// </summary>
        public override void OnSerialised(bool xmlSerialisation)
        {
            if (Script != null)
                Models.Add(Script);
        }

        /// <summary>
        /// Rebuild the script model and return error message if script cannot be compiled.
        /// </summary>
        public void RebuildScriptModel()
        {
            if (HasDeserialised)
            {
                // Capture the current values of all parameters.
                EnsureParametersAreCurrent();

                if (NeedToCompileCode())
                {
                    // If a script model already exists, then get rid of it.
                    if (Script != null)
                    {
                        Children.Remove(Script);
                        Script = null;
                    }

                    // Compile the code.
                    Assembly compiledAssembly;
                    try
                    {
                        compiledAssembly = Utility.Reflection.CompileTextToAssembly(Code, null);
                    }
                    catch (Exception err)
                    {
                        throw new ApsimXException(FullPath, err.Message);
                    }

                    CompiledCode = _Code;

                    // Get the script 'Type' from the compiled assembly.
                    Type scriptType = compiledAssembly.GetType("Models.Script");
                    if (scriptType == null)
                        throw new ApsimXException(FullPath, "Cannot find a public class called 'Script'");

                    // Create a new script model.
                    Script = compiledAssembly.CreateInstance("Models.Script") as Model;
                    Script.Models = new System.Collections.Generic.List<Model>();
                    Script.Name = "Script";
                    XmlElement parameters;
                    if (elements == null || elements[0] == null)
                    {
                        XmlDocument doc = new XmlDocument();
                        doc.LoadXml(elementsAsXml);
                        parameters = doc.DocumentElement;
                    }
                    else
                        parameters = elements[0];
                    SetParametersInObject(Script, parameters);

                    // Add the new script model to our models collection.
                    Children.Add(Script);
                    Script.HiddenModel = true;


                }
            }
        }



        /// <summary>
        /// Return true if the code needs to be recompiled.
        /// </summary>
        private bool NeedToCompileCode()
        {
            return (Script == null || _Code != CompiledCode);
        }

        /// <summary>
        /// Ensures the parameters are up to date and reflect the current 'Script' model.
        /// </summary>
        private void EnsureParametersAreCurrent()
        {
            if (Script != null)
            {
                if (elements == null)
                    elements = new XmlElement[1];
                elements[0] = GetParametersInObject(Script);
            }
            else if (elementsAsXml == null && elements != null && elements.Length >= 1)
                elementsAsXml = elements[0].OuterXml;
            else if (elementsAsXml == null)
                elementsAsXml = "<Script />";
        }

        /// <summary>
        /// Set the scripts parameters from the 'xmlElement' passed in.
        /// </summary>
        private void SetParametersInObject(Model script, XmlElement xmlElement)
        {
            foreach (XmlElement element in xmlElement.ChildNodes)
            {
                PropertyInfo property = Script.GetType().GetProperty(element.Name);
                if (property != null)
                    property.SetValue(script, Utility.Reflection.StringToObject(property.PropertyType, element.InnerText), null);
            }
        }

        /// <summary>
        /// Get the scripts parameters as a returned xmlElement.
        /// </summary>
        private XmlElement GetParametersInObject(Model script)
        {
            XmlDocument doc = new XmlDocument();
            doc.AppendChild(doc.CreateElement("Script"));
            foreach (PropertyInfo property in script.GetType().GetProperties(BindingFlags.Instance | BindingFlags.DeclaredOnly | BindingFlags.Public))
            {
                if (property.CanRead && property.CanWrite && 
                    Utility.Reflection.GetAttribute(property, typeof(XmlIgnoreAttribute), false) == null)
                {
                    object value = property.GetValue(script, null);
                    if (value == null)
                        value = "";
                    Utility.Xml.SetValue(doc.DocumentElement, property.Name, 
                                         Utility.Reflection.ObjectToString(value));
                }
            }
            return doc.DocumentElement;
        }


    }
}