﻿/////////////////////////////////////////////////////////////////////
// Copyright (c) Autodesk, Inc. All rights reserved
// Written by Forge Partner Development
//
// Permission to use, copy, modify, and distribute this software in
// object code form for any purpose and without fee is hereby granted,
// provided that the above copyright notice appears in all copies and
// that both that copyright notice and the limited warranty and
// restricted rights notice below appear in all supporting
// documentation.
//
// AUTODESK PROVIDES THIS PROGRAM "AS IS" AND WITH ALL FAULTS.
// AUTODESK SPECIFICALLY DISCLAIMS ANY IMPLIED WARRANTY OF
// MERCHANTABILITY OR FITNESS FOR A PARTICULAR USE.  AUTODESK, INC.
// DOES NOT WARRANT THAT THE OPERATION OF THE PROGRAM WILL BE
// UNINTERRUPTED OR ERROR FREE.
/////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Path = System.IO.Path;

using Inventor;
using Newtonsoft.Json;
using File = System.IO.File;
using System.Threading;

namespace samplePlugin
{
    [ComVisible(true)]
    public class SampleAutomation
    {
        InventorServer inventorApplication;

        public SampleAutomation(InventorServer inventorApp)
        {
            inventorApplication = inventorApp;
        }

        public void Run(Document doc)
        {
            LogTrace("Run called with {0}", doc.DisplayName);
            File.AppendAllText("output.txt", "Document name: " + doc.DisplayName);
        }

        private class HeartBeat : IDisposable
        {
            // default is 50s
            public HeartBeat(int intervalMillisec=50000)
            {
                t = new Thread(() => {

                    LogTrace("HeartBeating every {0}ms.", intervalMillisec);

                    for (; ; )
                    {
                        Thread.Sleep((int)intervalMillisec);
                        LogTrace("HeartBeat {0}.", (long)(new TimeSpan(DateTime.Now.Ticks - ticks).TotalSeconds));
                    }

                });

                ticks = DateTime.Now.Ticks;
                t.Start();
            }

            public void Dispose()
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }

            protected virtual void Dispose(bool disposing)
            {
                if (disposing)
                {
                    if (t != null)
                    {
                        LogTrace("Ending HeartBeat");
                        t.Abort();
                        t = null;
                    }
                }
            }

            private Thread t;
            private long ticks;
        }

        public void RunWithArguments(Document doc, NameValueMap map)
        {
            // write diagnostics data
            LogInputData(doc, map);

            var pathName = doc.FullFileName;
            LogTrace("Processing " + pathName);

            try
            {
                // load processing parameters
                string paramsJson = GetParametersToChange(map);

                // update parameters in the doc
                // start HeartBeat around ChangeParameters, it could be a long operation
                using (new HeartBeat())
                {
                    ChangeParameters(doc, paramsJson);
                }

                // generate outputs
                var docDir = Path.GetDirectoryName(doc.FullFileName);

                var documentType = doc.DocumentType;
                if (documentType == DocumentTypeEnum.kPartDocumentObject)
                {
                    var fileName = Path.Combine(docDir, "Result.ipt"); // the name must be in sync with OutputIpt localName in Activity
                    LogTrace("Saving " + fileName);
                    // start HeartBeat around Save, it could be a long operation
                    using (new HeartBeat())
                    {
                        doc.SaveAs(fileName, false);
                    }
                    LogTrace("Saved as " + fileName);

                    // save an image
                    SaveImageFromPart(Path.Combine(docDir, "Result.bmp"), doc as PartDocument);
                }
                else // Assembly. That's already validated in ChangeParameters
                {
                    // cannot ZIP opened assembly, so close it
                    // start HeartBeat around Save, it could be a long operation
                    using (new HeartBeat())
                    {
                        doc.Save2(true);
                    }
                    doc.Close(true);

                    LogTrace("Zipping up updated Assembly.");

                    // assembly lives in own folder under WorkingDir. Get the WorkingDir
                    var workingDir = Path.GetDirectoryName(docDir);
                    var fileName = Path.Combine(workingDir, "Result.zip"); // the name must be in sync with OutputIam localName in Activity

                    // start HeartBeat around ZipFile, it could be a long operation
                    using (new HeartBeat())
                    {
                        ZipFile.CreateFromDirectory(Path.GetDirectoryName(pathName), fileName, CompressionLevel.Fastest, false);
                    }

                    LogTrace($"Saved as {fileName}");
                }
            }
            catch (Exception e)
            {
                LogError("Processing failed. " + e.ToString());
            }
        }

        private void SaveImageFromPart(string filePath, PartDocument partDoc)
        {
            LogTrace($"Saving image {filePath}");
            Camera cam = inventorApplication.TransientObjects.CreateCamera();
            cam.SceneObject = partDoc.ComponentDefinition;
            cam.ViewOrientationType = ViewOrientationTypeEnum.kIsoTopRightViewOrientation;
            cam.ApplyWithoutTransition();
            cam.SaveAsBitmap(filePath, 200, 200, Type.Missing, Type.Missing);
            LogTrace($"Saved image as {filePath}");
        }

        /// <summary>
        /// First param "_1" should be the filename of the JSON file containing the parameters and values
        /// </summary>
        /// <returns>
        /// JSON with parameters.
        /// JSON content sample:
        ///   { "SquarePegSize": "0.24 in" }
        /// </returns>
        private static string GetParametersToChange(NameValueMap map)
        {
            string paramFile = (string) map.Value["_1"];
            string json = File.ReadAllText(paramFile);
            LogTrace("Inventor Parameters JSON: \"" + json + "\"");
            return json;
        }

        /// <summary>
        /// Change parameters in Inventor document.
        /// </summary>
        /// <param name="doc">The Inventor document.</param>
        /// <param name="json">JSON with changed parameters.</param>
        public void ChangeParameters(Document doc, string json)
        {
            var theParams = GetParameters(doc);

            Dictionary<string, string> parameters = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
            foreach (KeyValuePair<string, string> entry in parameters)
            {
                var parameterName = entry.Key;
                var expression = entry.Value;

                LogTrace("Parameter to change: {0}:{1}", parameterName, expression);

                try
                {
                    Parameter param = theParams[parameterName];
                    param.Expression = expression;
                }
                catch (Exception e)
                {
                    LogError("Cannot update '{0}' parameter. ({1})", parameterName, e.Message);
                }
            }

            doc.Update();
            doc.Save();

            LogTrace("Doc updated.");
        }

        /// <summary>
        /// Get parameters for the document.
        /// </summary>
        /// <returns>Parameters. Throws exception if parameters are not found.</returns>
        private static Parameters GetParameters(Document doc)
        {
            var docType = doc.DocumentType;
            switch (docType)
            {
                case DocumentTypeEnum.kAssemblyDocumentObject:
                    var asm = doc as AssemblyDocument;
                    return asm.ComponentDefinition.Parameters;

                case DocumentTypeEnum.kPartDocumentObject:
                    var ipt = doc as PartDocument;
                    return ipt.ComponentDefinition.Parameters;

                default:
                    throw new ApplicationException(string.Format("Unexpected document type ({0})", docType));
            }
        }

        /// <summary>
        /// Write info on input data to log.
        /// </summary>
        private static void LogInputData(Document doc, NameValueMap map)
        {
            // dump doc name
            var traceInfo = new StringBuilder("RunWithArguments called with '");
            traceInfo.Append(doc.DisplayName);

            traceInfo.Append("'. Parameters: ");

            // dump input parameters
            // values in map are keyed on _1, _2, etc
            string[] parameterValues = Enumerable
                                        .Range(1, map.Count)
                                        .Select(i => (string) map.Value["_" + i])
                                        .ToArray();
            string values = string.Join(", ", parameterValues);
            traceInfo.Append(values);
            traceInfo.Append(".");

            LogTrace(traceInfo.ToString());
        }

        #region Logging utilities

        /// <summary>
        /// Log message with 'trace' log level.
        /// </summary>
        private static void LogTrace(string format, params object[] args)
        {
            Trace.TraceInformation(format, args);
        }

        /// <summary>
        /// Log message with 'trace' log level.
        /// </summary>
        private static void LogTrace(string message)
        {
            Trace.TraceInformation(message);
        }

        /// <summary>
        /// Log message with 'error' log level.
        /// </summary>
        private static void LogError(string format, params object[] args)
        {
            Trace.TraceError(format, args);
        }

        /// <summary>
        /// Log message with 'error' log level.
        /// </summary>
        private static void LogError(string message)
        {
            Trace.TraceError(message);
        }

        #endregion
    }
}