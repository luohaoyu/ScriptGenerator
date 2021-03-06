﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml;
using Abstracta.FiddlerSessionComparer;
using Abstracta.Generators.Framework.AbstractGenerator.Wrappers;
using Abstracta.Generators.Framework.OSTAGenerator;
using Fiddler;

namespace Abstracta.Generators.Framework
{
    public enum GeneratorType
    {
        JMeter,
        OpenSTA,
        Testing,
    }

    public class ScriptGenerator
    {
        internal readonly string OutputPath, DataPoolsPath, ScriptName;
        internal readonly bool IsBMScript;
        internal readonly GxTestScriptWrapper ScriptWrapper;
        internal readonly FiddlerSessionsWrapper[] FiddlerSessions;

        internal readonly string ServerName, WebAppName;

        private readonly Page _resultOfComparer;

        private static readonly string[] Extenssions = new[] { "html", "htm", "asp", "jsp", "php" };

        public static Session[] GetSessionsFromFile(string fiddlerSessionsFileName)
        {
            return FiddlerSessionComparer.FiddlerSessionComparer.GetSessionsFromFile(fiddlerSessionsFileName);
        }

        /// <summary>
        /// JMeterGenerator constructor. Use to initialize the generator with all information it will need to generate the scripts
        /// </summary>
        /// <param name="outputPath">Folder where the generator will place the scripts it will create</param>
        /// <param name="dataPoolsPath"></param>
        /// <param name="performanceScript">GXTest XML info file that shows steps, validations and other commands</param>
        /// <param name="fiddlerSessions">HTTP Sessions given by fiddler</param>
        /// <param name="server">serverName:PortNumber</param>
        /// <param name="webApp">WebAppName</param>
        /// <param name="isGenexusApp">Indicates if the app is a genexus generated app. Taking some decisions for that case.</param>
        /// <param name="isBMScript">Indicates if the script is for BlazeMeter. Taking some decisions for that case.</param>
        /// <param name="replaceInBodies"></param>
        /// <param name="ext"></param>
        public ScriptGenerator(string outputPath, string dataPoolsPath, XmlDocument performanceScript, IList<Session[]> fiddlerSessions, string server, string webApp, bool isGenexusApp, bool isBMScript = false, bool replaceInBodies = false, IEnumerable<string> ext = null)
        {
            OutputPath = outputPath;
            DataPoolsPath = dataPoolsPath;
            IsBMScript = isBMScript; ;

            var extenssions = ext == null? Extenssions : Extenssions.Concat(ext).ToArray();

            if (fiddlerSessions == null)
            {
                throw new Exception("Fiddler Sessions is null");
            }

            if (fiddlerSessions.Count < 1)
            {
                throw new Exception("Fiddler Sessions is empty");
            }

            if (performanceScript == null)
            {
                // todo: manage datapools of fiddler extension
                string[] datapoolsPath = { };
                performanceScript = GenerateXML(fiddlerSessions[0], "_AutogeneratedName", datapoolsPath);
            }

            ScriptWrapper = new GxTestScriptWrapper(performanceScript);
            FiddlerSessions = new FiddlerSessionsWrapper[fiddlerSessions.Count];
            for (var i = 0; i < fiddlerSessions.Count; i++)
            {
                FiddlerSessions[i] = new FiddlerSessionsWrapper(fiddlerSessions[i]);
            }

            ServerName = server;
            WebAppName = webApp;

            ScriptName = ScriptWrapper.ScriptName;

            // compare sessions once in the constructor of the class
            if (FiddlerSessions.Length > 1)
            {
                var fiddlerComparer = new FiddlerSessionComparer.FiddlerSessionComparer(replaceInBodies, isGenexusApp);
                fiddlerComparer.Load(FiddlerSessions[0].GetSessions(), FiddlerSessions[1].GetSessions(), extenssions);
                _resultOfComparer = fiddlerComparer.CompareFull();

                for (var i = 2; i < FiddlerSessions.Length; i++)
                {
                    _resultOfComparer = fiddlerComparer.CompareFull(FiddlerSessions[i].GetSessions());
                }
            }

            //// if (debug)
            ////if (_resultOfComparer != null)
            ////{
            ////    const string fName = @"D:\Abstracta\Desarrollo\gxtest\BranchGeneradoras\Generadoras\TestCases\ASESP3\pages.txt";
            ////    (new StreamWriter(fName)).Write(_resultOfComparer.ToString("", false));
            ////}
        }

        public void GenerateScripts(GeneratorType generatorType)
        {
            FiddlerSessionComparer.FiddlerSessionComparer.ResetComparer();
            var generator = GetGenerator(generatorType);

            // Initialize generator
            generator.Initialize(OutputPath, ScriptName, ServerName, WebAppName, IsBMScript);

            // add dataPools to generator
            var dataPools = ScriptWrapper.GetDataPools();
            generator.AddDataPools(dataPools, DataPoolsPath);

            var stepIndex = 0;

            // add each command as a step in the generator
            var commands = ScriptWrapper.GetCommands();
            foreach (var command in commands)
            {
                switch (command.Type)
                {
                    case "Action":
                    case "Event":
                        {
                            var step = generator.AddStep(command.Name, command.Type, command.Desc, this, stepIndex);
                            foreach (var httpReq in command.RequestIds.Select(requestId => FiddlerSessions[0].GetRequest(requestId)))
                            {
                                step.AddRequest(httpReq, _resultOfComparer);
                            }

                            stepIndex++;
                        }
                        break;
                    case "Validation":
                        {
                            var lastStep = generator.GetLastStep();
                            if (lastStep != null)
                            {
                                lastStep.AddValidation(command);
                            }
                            else
                            {
                                throw new Exception("There is a validation before the first Step :S");
                            }
                        }
                        break;
                }
            }

            // Save results to files
            generator.Save();
        }

        private static AbstractGenerator.AbstractGenerator GetGenerator(GeneratorType generatorType)
        {
            switch (generatorType)
            {
                case GeneratorType.JMeter:
                    return new JMeterGenerator.JMeterGenerator();

                case GeneratorType.OpenSTA:
                    return new OpenSTAGenerator();

                case GeneratorType.Testing:
                    return new TestingGenerator.TestingGenerator();

                default:
                    throw new NotImplementedException("GeneratorType is not implemented: " + generatorType);
            }
        }

        private static XmlDocument GenerateXML(IList<Session> sessions, string scriptName, IEnumerable<string> datapoolsPath)
        {
            try
            {
                var error = false;

                //creates the xml temporary document containing the metadata for the script generator
                var xmlDoc = new XmlDocument();

                //creates the root node
                var rootNode = xmlDoc.CreateElement("ParentTestCase");
                rootNode.SetAttribute("Name", scriptName);
                rootNode.SetAttribute("xmlversion", "1.0");

                //creates a list used to reference the temporary datapool files
                var dataPoolsRoute = new List<string>();

                //creates the datapools node, adding a datapool element for each csv file imported
                var dataPoolsNode = xmlDoc.CreateElement("DataPools");
                foreach (var s in datapoolsPath)
                {
                    //skips empty paths and checks if an error ocurred on a previous file import
                    if (s.Equals("") || error) continue;

                    //creates the datapool node, containing a reference to a copy of the imported file
                    var dataPoolNode = xmlDoc.CreateElement("DataPool");
                    var route = s.Split('\\');
                    var fName = (String)route.GetValue(route.Length - 1);
                    dataPoolNode.SetAttribute("Name", fName.Replace(".csv", ""));
                    dataPoolNode.SetAttribute("File", fName);

                    try
                    {
                        //opens the imported file and reads the first line, which should contain the csv headers
                        //headers must be alfabetic chars
                        var fsr = new FileStream(s, FileMode.Open);
                        var sr = new StreamReader(fsr);
                        var line = sr.ReadLine();
                        //checks that imported file is not null
                        if (line != null)
                        {
                            //obtains the headers of the csv file
                            foreach (var c in line.Split(','))
                            {
                                //creates a datapool column node containing the column name and adds the node to the datapool node
                                var dataPoolColumn = xmlDoc.CreateElement("DataPoolColumn");
                                dataPoolColumn.SetAttribute("Name", c);
                                dataPoolNode.AppendChild(dataPoolColumn);
                            }

                            // creates a temporary file containing a copy of the imported file, except for the headers
                            var fileRoute = fName;
                                
                            var fsw = new FileStream(fileRoute, FileMode.Create, FileAccess.Write);
                            using (var sw = new StreamWriter(fsw))
                            {
                                sw.Write(sr.ReadToEnd());
                            }

                            dataPoolsRoute.Add(fileRoute);
                        }
                        else
                        {
                            //shows empty file error message
                            MessageBox.Show("Error in file '" + fName + "'\nFile is empty.");
                            error = true;
                        }

                        //closes the imported file
                        sr.Close();
                    }
                    catch (Exception ex)
                    {
                        //shows unexpected exception message
                        MessageBox.Show(ex.ToString());
                        error = true;
                    }

                    //adds the datapool node corresponding to the imported file
                    dataPoolsNode.AppendChild(dataPoolNode);
                }

                //checks that nothing went wrong when creating the datapool nodes and adds the datapools node to the root node
                if (!error)
                {
                    rootNode.AppendChild(dataPoolsNode);

                    //creates a command node containing information about the script structure
                    var commandNode = xmlDoc.CreateElement("Command");

                    commandNode.SetAttribute("CommandName", "");
                    commandNode.SetAttribute("CommandType", "");
                    commandNode.SetAttribute("CommandDsc", "");

                    //creates a requests node, which will contain the requests captured in fiddler for a certain command
                    var requestsNode = xmlDoc.CreateElement("Requests");

                    //creates a parameters node
                    //note: the script generator expects to find a parameters tag
                    var parametersNode = xmlDoc.CreateElement("Parameters");

                    //initializes a string that contains the comment of the previous session
                    var prevComment = "";
                    XmlElement iTc = null;

                    //creates a request node for each request obtained from fiddler, and adds it to the requests node.
                    //commands are identified by the content of the comment field
                    for (var i = 0; i < sessions.Count; i++)
                    {
                        var comment = sessions[i].oFlags["ui-comments"] ?? "";

                        comment = comment.Trim().Replace(' ', '_').ToLower();
                        if (!prevComment.Equals(comment))
                        {
                            //adds the requests node to the command node
                            commandNode.AppendChild(requestsNode);

                            //adds a parameters node to the command node
                            commandNode.AppendChild(parametersNode);

                            //if the comment indicates a login is finished, the command node is added to the ITC node,
                            //and the ITC node is added to the root node. if not, it is added to the root node directly
                            if (prevComment.Equals("login") || comment.Equals("log_in") || comment.Equals("log-in"))
                            {
                                if (iTc != null)
                                {
                                    iTc.AppendChild(commandNode);
                                    rootNode.AppendChild(iTc);
                                }
                                else
                                {
                                    throw new Exception("ITC is NULL !!");
                                }
                            }
                            else
                            {
                                rootNode.AppendChild(commandNode);
                            }

                            //if the comment indicates a login is starting, the requests are added in an included test case.
                            //if not, they are added in a command node to the main script
                            if (comment.Equals("login") || comment.Equals("log_in") || comment.Equals("log-in"))
                            {
                                iTc = xmlDoc.CreateElement("IncludedTestCase");
                                iTc.SetAttribute("Name", "LogIn");
                            }

                            //creates a new command node identified by the sessions comment
                            commandNode = xmlDoc.CreateElement("Command");

                            commandNode.SetAttribute("CommandName", comment);
                            commandNode.SetAttribute("CommandType", "Event");
                            commandNode.SetAttribute("CommandDsc", comment);

                            //creates a new requests node, which will contain the requests captured in fiddler for this command
                            requestsNode = xmlDoc.CreateElement("Requests");

                            //creates a parameters node
                            parametersNode = xmlDoc.CreateElement("Parameters");

                            //updates the previous comment
                            prevComment = comment;
                        }

                        var requestNode = xmlDoc.CreateElement("Request");
                        requestNode.SetAttribute("Id", i.ToString(CultureInfo.InvariantCulture));
                        requestsNode.AppendChild(requestNode);
                    }

                    //adds the requests node to the last command node
                    commandNode.AppendChild(requestsNode);

                    //adds a parameters node to the command node
                    commandNode.AppendChild(parametersNode);

                    //adds the command node to the root node, the root node to the xml file, and saves the temporary xml file
                    rootNode.AppendChild(commandNode);
                    xmlDoc.AppendChild(rootNode);

                    //deletes all the temporary csv files
                    foreach (var route in dataPoolsRoute.Where(File.Exists))
                    {
                        try
                        {
                            File.Delete(route);
                        }
                        catch (IOException exception)
                        {
                            //shows unable to delete file error message
                            var s = route.Split('\\');
                            var fName = (String)s.GetValue(route.Length - 1);
                            MessageBox.Show("Unable to delete '" + fName + "'\n" + exception.Message);
                        }
                    }

                    return xmlDoc;
                }

                throw new Exception("Error al generar el XML. Se aborta la ejecución");
            }
            catch (Exception e)
            {
                throw new Exception("Se pudrió todo: " + e.Message);
            }
        }
    }
}
