﻿// --------------------------------------------------------------------------------------------------------------------
// <summary>
//   UIMF Data Writer class
//
//   Originally written by Yan Shi for the Department of Energy (PNNL, Richland, WA)
//   Additional contributions by Anuj Shah, Matthew Monroe, Gordon Slysz, Kevin Crowell, Bill Danielson, Spencer Prost, and Bryson Gibbons
//   E-mail: matthew.monroe@pnnl.gov or proteomics@pnnl.gov
//   Website: https://omics.pnl.gov/ or https://www.pnnl.gov/sysbio/ or https://panomics.pnnl.gov/
//
// </summary>
// --------------------------------------------------------------------------------------------------------------------

using System.Collections;
using System.Data;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Reflection;

// ReSharper disable UnusedMember.Global

namespace UIMFLibrary
{
    using System;
    using System.Collections.Generic;
    using System.Data.SQLite;
    using System.Text;

    /// <summary>
    /// UIMF Data Writer class
    /// </summary>
    public class DataWriter : UIMFData
    {
        #region Constants

        /// <summary>
        /// Minimum interval between flushing (commit transaction / create new transaction)
        /// </summary>
        private const int MINIMUM_FLUSH_INTERVAL_SECONDS = 5;

        #endregion

        #region Fields

        /// <summary>
        /// Command to insert a frame parameter key
        /// </summary>
        private SQLiteCommand mDbCommandInsertFrameParamKey;

        /// <summary>
        /// Command to insert a frame parameter value
        /// </summary>
        private SQLiteCommand mDbCommandInsertFrameParamValue;

        /// <summary>
        /// Command to update a frame parameter value
        /// </summary>
        private SQLiteCommand mDbCommandUpdateFrameParamValue;

        /// <summary>
        /// Command to insert a row in the legacy FrameParameters table
        /// </summary>
        private SQLiteCommand mDbCommandInsertLegacyFrameParameterRow;

        /// <summary>
        /// Command to insert a global parameter value
        /// </summary>
        private SQLiteCommand mDbCommandInsertGlobalParamValue;

        /// <summary>
        /// Command to update a global parameter value
        /// </summary>
        private SQLiteCommand mDbCommandUpdateGlobalParamValue;

        /// <summary>
        /// Command to insert a scan
        /// </summary>
        private SQLiteCommand mDbCommandInsertScan;

        private DateTime mLastFlush;

        /// <summary>
        /// Whether or not to create the legacy Global_Parameters and Frame_Parameters tables
        /// </summary>
        private bool mCreateLegacyParametersTables;

        private bool mLegacyGlobalParametersTableHasData;

        private bool mLegacyFrameParameterTableHasDecodedColumn;
        private bool mLegacyFrameParameterTableHaHPFColumns;

        /// <summary>
        /// This list tracks the frame numbers that are present in the Frame_Parameters table
        /// </summary>
        private readonly SortedSet<int> mFrameNumsInLegacyFrameParametersTable;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="DataWriter"/> class.
        /// Constructor for UIMF DataWriter that takes the filename and begins the transaction.
        /// </summary>
        /// <param name="filePath">Full path to the data file</param>
        /// <param name="entryAssembly">Entry assembly, used when adding a line to the Version_Info table</param>
        /// <remarks>When creating a brand new .UIMF file, you must call CreateTables() after instantiating the writer</remarks>
        public DataWriter(string filePath, Assembly entryAssembly)
            : this(filePath, true, entryAssembly)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DataWriter"/> class.
        /// Constructor for UIMF DataWriter that takes the filename and begins the transaction.
        /// </summary>
        /// <param name="filePath">Full path to the data file</param>
        /// <param name="createLegacyParametersTables">When true, create and populate legacy tables Global_Parameters and Frame_Parameters</param>
        /// <param name="entryAssembly">Entry assembly, used when adding a line to the Version_Info table</param>
        /// <remarks>When creating a brand new .UIMF file, you must call CreateTables() after instantiating the writer</remarks>
        public DataWriter(string filePath, bool createLegacyParametersTables = true, Assembly entryAssembly = null)
            : base(filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("UIMF file path cannot be empty", nameof(filePath));

            mCreateLegacyParametersTables = createLegacyParametersTables;
            mFrameNumsInLegacyFrameParametersTable = new SortedSet<int>();

            var usingExistingDatabase = File.Exists(mFilePath);

            // Note: providing true for parseViaFramework as a workaround for reading SqLite files located on UNC or in readonly folders
            var connectionString = "Data Source = " + filePath + "; Version=3; DateTimeFormat=Ticks;";
            mDbConnection = new SQLiteConnection(connectionString, true);
            mLastFlush = DateTime.UtcNow;

            try
            {
                mDbConnection.Open();

                TransactionBegin();

                PrepareInsertFrameParamKey();
                PrepareInsertFrameParamValue();
                PrepareUpdateFrameParamValue();
                PrepareInsertGlobalParamValue();
                PrepareUpdateGlobalParamValue();
                PrepareInsertScan();
                PrepareInsertLegacyFrameParamValue();

                mFrameParameterKeys = new Dictionary<FrameParamKeyType, FrameParamDef>();

                if (HasLegacyParameterTables)
                {
                    // The tables exist, so
                    mCreateLegacyParametersTables = true;
                }

                // If table Global_Parameters exists and table Global_Params does not exist, create Global_Params using Global_Parameters
                ConvertLegacyGlobalParameters();

                if (usingExistingDatabase && mGlobalParameters.Values.Count == 0)
                {
                    CacheGlobalParameters();
                }

                // Read the frame numbers in the legacy Frame_Parameters table to make sure that mFrameNumsInLegacyFrameParametersTable is up to date
                CacheLegacyFrameNums();

                // If table Frame_Parameters exists and table Frame_Params does not exist, then create Frame_Params using Frame_Parameters
                ConvertLegacyFrameParameters();

                // Make sure the Version_Info table exists
                if (!HasVersionInfoTable)
                {
                    using (var dbCommand = mDbConnection.CreateCommand())
                    {
                        CreateVersionInfoTable(dbCommand, entryAssembly);
                    }
                }
                else
                {
                    AddVersionInfo(entryAssembly);
                }

            }
            catch (Exception ex)
            {
                ReportError(string.Format("Failed to open UIMF file {0}: {1}", filePath, ex.Message), ex);
                throw;
            }
        }

        private void CacheLegacyFrameNums()
        {
            try
            {

                if (!HasLegacyParameterTables)
                {
                    // Nothing to do
                    return;
                }

                using (var dbCommand = mDbConnection.CreateCommand())
                {
                    dbCommand.CommandText = "SELECT FrameNum FROM " + FRAME_PARAMETERS_TABLE + " ORDER BY FrameNum;";
                    var reader = dbCommand.ExecuteReader();

                    while (reader.Read())
                    {
                        var frameNum = reader.GetInt32(0);

                        if (!mFrameNumsInLegacyFrameParametersTable.Contains(frameNum))
                            mFrameNumsInLegacyFrameParametersTable.Add(frameNum);
                    }

                }

            }
            catch (Exception ex)
            {
                CheckExceptionForIntermittentError(ex, "CacheLegacyFrameNums");
                ReportError(
                    "Exception caching the frame numbers in the legacy Frame_Parameters table: " + ex.Message, ex);
                throw;
            }


        }

        private void ConvertLegacyFrameParameters()
        {

            var framesProcessed = 0;
            var currentTask = "Initializing";

            try
            {
                if (HasFrameParamsTable)
                {
                    // Assume that the frame parameters have already been converted
                    // Nothing to do
                    return;
                }

                if (!HasLegacyParameterTables)
                {
                    // Legacy tables do not exist; nothing to do
                    return;
                }

                // Make sure writing of legacy parameters is turned off
                var createLegacyParametersTablesSaved = mCreateLegacyParametersTables;
                mCreateLegacyParametersTables = false;

                Console.WriteLine("\nCreating the Frame_Params table using the legacy frame parameters");
                var lastUpdate = DateTime.UtcNow;

                // Keys in this array are frame number, values are the frame parameters
                var cachedFrameParams = new Dictionary<int, FrameParams>();

                // Read and cache the legacy frame parameters
                currentTask = "Caching existing parameters";

                using (var reader = new DataReader(mFilePath))
                {
                    reader.PreCacheAllFrameParams();

                    var frameList = reader.GetMasterFrameList();

                    foreach (var frameInfo in frameList)
                    {
                        var frameParams = reader.GetFrameParams(frameInfo.Key);
                        cachedFrameParams.Add(frameInfo.Key, frameParams);

                        framesProcessed++;

                        if (DateTime.UtcNow.Subtract(lastUpdate).TotalSeconds >= 5)
                        {
                            Console.WriteLine(" ... caching frame parameters, " + framesProcessed + " / " +
                                              frameList.Count);
                            lastUpdate = DateTime.UtcNow;
                        }
                    }

                }

                Console.WriteLine();

                currentTask = "Creating Frame_Params table";
                using (var dbCommand = mDbConnection.CreateCommand())
                {
                    // Create the Frame_Param_Keys and Frame_Params tables
                    CreateFrameParamsTables(dbCommand);
                }

                framesProcessed = 0;

                // Store the frame parameters
                currentTask = "Storing parameters in Frame_Params";
                foreach (var frameParamsEntry in cachedFrameParams)
                {
                    var frameParams = frameParamsEntry.Value;

                    var frameParamsLite = new Dictionary<FrameParamKeyType, dynamic>();
                    foreach (var paramEntry in frameParams.Values)
                    {
                        frameParamsLite.Add(paramEntry.Key, paramEntry.Value.Value);
                    }

                    InsertFrame(frameParamsEntry.Key, frameParamsLite);

                    framesProcessed++;
                    if (DateTime.UtcNow.Subtract(lastUpdate).TotalSeconds >= 5)
                    {
                        Console.WriteLine(" ... storing frame parameters, " + framesProcessed + " / " +
                                          cachedFrameParams.Count);
                        lastUpdate = DateTime.UtcNow;
                    }

                }

                Console.WriteLine("Conversion complete\n");

                // Possibly turn back on Legacy parameter writing
                mCreateLegacyParametersTables = createLegacyParametersTablesSaved;

                FlushUimf(true);
            }
            catch (Exception ex)
            {
                CheckExceptionForIntermittentError(ex, "ConvertLegacyFrameParameters");
                ReportError(
                    "Exception creating the Frame_Params table using existing table Frame_Parameters " +
                    "(current task '" + currentTask + "', processed " + framesProcessed + " frames): " + ex.Message, ex);
                throw;
            }

        }

        /// <summary>
        /// Create and populate table Global_Params using legacy table Global_Parameters
        /// </summary>
        private void ConvertLegacyGlobalParameters()
        {
            try
            {
                if (HasGlobalParamsTable)
                {
                    // Assume that the global parameters have already been converted
                    // Nothing to do
                    return;
                }

                if (!HasLegacyParameterTables)
                {
                    // Nothing to do
                    return;
                }

                // Make sure writing of legacy parameters is turned off
                var createLegacyParametersTablesSaved = mCreateLegacyParametersTables;
                mCreateLegacyParametersTables = false;

                // Keys in this array are frame number, values are the frame parameters
                GlobalParams cachedGlobalParams;

                // Read and cache the legacy global parameters
                using (var reader = new DataReader(mFilePath))
                {
                    cachedGlobalParams = reader.GetGlobalParams();
                }

                using (var dbCommand = mDbConnection.CreateCommand())
                {
                    // Create the Global_Params table
                    CreateGlobalParamsTable(dbCommand);
                }

                // Store the global parameters
                foreach (var globalParam in cachedGlobalParams.Values)
                {
                    var currentParam = globalParam.Value;

                    AddUpdateGlobalParameter(currentParam.ParamType, currentParam.Value);
                }

                FlushUimf(false);

                // Possibly turn back on Legacy parameter writing
                mCreateLegacyParametersTables = createLegacyParametersTablesSaved;

            }
            catch (Exception ex)
            {
                CheckExceptionForIntermittentError(ex, "ConvertGlobalParameters");
                ReportError(
                    "Exception creating the Global_Params table using existing table Global_Parameters: " + ex.Message, ex);
                throw;
            }

        }

        #endregion

        #region Public Methods and Operators

        /// <summary>
        /// Post a new log entry to table Log_Entries
        /// </summary>
        /// <param name="entryType">
        /// Log entry type (typically Normal, Error, or Warning)
        /// </param>
        /// <param name="message">
        /// Log message
        /// </param>
        /// <param name="postedBy">
        /// Process or application posting the log message
        /// </param>
        /// <remarks>
        /// The Log_Entries table will be created if it doesn't exist
        /// </remarks>
        public void PostLogEntry(string entryType, string message, string postedBy)
        {
            // Check whether the Log_Entries table needs to be created
            using (var cmdPostLogEntry = mDbConnection.CreateCommand())
            {

                if (!TableExists(mDbConnection, "Log_Entries"))
                {
                    // Log_Entries not found; need to create it
                    cmdPostLogEntry.CommandText = "CREATE TABLE Log_Entries ( " +
                                                  "Entry_ID INTEGER PRIMARY KEY, " +
                                                  "Posted_By STRING, " +
                                                  "Posting_Time STRING, " +
                                                  "Type STRING, " +
                                                  "Message STRING)";

                    cmdPostLogEntry.ExecuteNonQuery();
                }

                if (string.IsNullOrEmpty(entryType))
                {
                    entryType = "Normal";
                }

                if (string.IsNullOrEmpty(postedBy))
                {
                    postedBy = string.Empty;
                }

                if (string.IsNullOrEmpty(message))
                {
                    message = string.Empty;
                }

                // Now add a log entry
                cmdPostLogEntry.CommandText = "INSERT INTO Log_Entries (Posting_Time, Posted_By, Type, Message) " +
                                              "VALUES ("
                                              + "datetime('now'), " + "'" + postedBy + "', " + "'" + entryType + "', " +
                                              "'"
                                              + message + "')";

                cmdPostLogEntry.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Post a new log entry to table Log_Entries
        /// </summary>
        /// <param name="oConnection">
        /// Database connection object
        /// </param>
        /// <param name="entryType">
        /// Log entry type (typically Normal, Error, or Warning)
        /// </param>
        /// <param name="message">
        /// Log message
        /// </param>
        /// <param name="postedBy">
        /// Process or application posting the log message
        /// </param>
        /// <remarks>
        /// The Log_Entries table will be created if it doesn't exist
        /// </remarks>
        [Obsolete("Use the non-static PostLogEntry function", true)]
        public static void PostLogEntry(SQLiteConnection oConnection, string entryType, string message, string postedBy)
        {
            throw new Exception("Static PostLogEntry is obsolete, use non-static PostLogEntry");
        }

        /// <summary>
        /// Creates the Global_Parameters and Frame_Parameters tables using existing data in tables Global_Params and Frame_Params
        /// </summary>
        /// <remarks>Does not add any values if the legacy tables already exist</remarks>
        public void AddLegacyParameterTablesUsingExistingParamTables()
        {

            if (HasLegacyParameterTables)
            {
                // Nothing to do
                return;
            }

            if (!HasFrameParamsTable)
            {
                // Nothing to do
                return;
            }

            Console.WriteLine("Caching GlobalParams and FrameParams");

            GlobalParams globalParams;
            var frameParamsList = new Dictionary<int, FrameParams>();

            using (var uimfReader = new DataReader(mFilePath))
            {
                globalParams = uimfReader.GetGlobalParams();
                var masterFrameList = uimfReader.GetMasterFrameList();

                uimfReader.PreCacheAllFrameParams();

                foreach (var frame in masterFrameList)
                {
                    var frameParams = uimfReader.GetFrameParams(frame.Key);
                    frameParamsList.Add(frame.Key, frameParams);
                }

            }

            using (var dbCommand = mDbConnection.CreateCommand())
            {
                CreateLegacyParameterTables(dbCommand);

                Console.WriteLine("Adding the Global_Parameters table");

                foreach (var globalParam in globalParams.Values)
                {
                    var paramEntry = globalParam.Value;
                    InsertLegacyGlobalParameter(dbCommand, paramEntry.ParamType, paramEntry.Value);
                }

                Console.WriteLine("Adding the Frame_Parameters table");

                foreach (var frameParams in frameParamsList)
                {
                    InsertLegacyFrameParams(frameParams.Key, frameParams.Value);
                }
            }

            FlushUimf(true);

        }

        /// <summary>
        /// Add or update a frame parameter entry in the Frame_Params table
        /// </summary>
        /// <param name="frameNum">Frame number</param>
        /// <param name="paramKeyType">Parameter type</param>
        /// <param name="paramValue">Parameter value</param>
        public DataWriter AddUpdateFrameParameter(int frameNum, FrameParamKeyType paramKeyType, string paramValue)
        {
            // Make sure the Frame_Param_Keys table contains key paramKeyType
            ValidateFrameParameterKey(paramKeyType);

            try
            {
                // SQLite does not have a merge statement
                // We therefore must first try an Update query
                // If no rows are matched, then run an insert query

                mDbCommandUpdateFrameParamValue.Parameters.Clear();
                mDbCommandUpdateFrameParamValue.Parameters.Add(new SQLiteParameter("FrameNum", frameNum));
                mDbCommandUpdateFrameParamValue.Parameters.Add(new SQLiteParameter("ParamID", (int)paramKeyType));
                mDbCommandUpdateFrameParamValue.Parameters.Add(new SQLiteParameter("ParamValue", paramValue));
                var updateCount = mDbCommandUpdateFrameParamValue.ExecuteNonQuery();

                if (mCreateLegacyParametersTables)
                {
                    using (var dbCommand = mDbConnection.CreateCommand())
                    {
                        if (!mFrameNumsInLegacyFrameParametersTable.Contains(frameNum))
                        {
                            // Check for an existing row in the legacy Frame_Parameters table for this frame
                            dbCommand.CommandText = "SELECT COUNT(*) FROM " + FRAME_PARAMETERS_TABLE + " WHERE FrameNum = " + frameNum;
                            var rowCount = (long)(dbCommand.ExecuteScalar());

                            if (rowCount < 1)
                            {
#pragma warning disable 612, 618
                                var legacyFrameParameters = new FrameParameters
                                {
                                    FrameNum = frameNum
                                };
#pragma warning restore 612, 618

                                InitializeFrameParametersRow(legacyFrameParameters);
                            }
                            mFrameNumsInLegacyFrameParametersTable.Add(frameNum);
                        }

                        UpdateLegacyFrameParameter(frameNum, paramKeyType, paramValue, dbCommand);
                    }
                }

                if (updateCount == 0)
                {
                    mDbCommandInsertFrameParamValue.Parameters.Clear();
                    mDbCommandInsertFrameParamValue.Parameters.Add(new SQLiteParameter("FrameNum", frameNum));
                    mDbCommandInsertFrameParamValue.Parameters.Add(new SQLiteParameter("ParamID", (int)paramKeyType));
                    mDbCommandInsertFrameParamValue.Parameters.Add(new SQLiteParameter("ParamValue", paramValue));
                    mDbCommandInsertFrameParamValue.ExecuteNonQuery();
                }

                FlushUimf(false);

            }
            catch (Exception ex)
            {
                CheckExceptionForIntermittentError(ex, "AddUpdateFrameParameter");
                ReportError(
                    "Error adding/updating parameter " + paramKeyType + " for frame " + frameNum + ": " + ex.Message, ex);
                throw;
            }

            return this;
        }

        /// <summary>
        /// Add or update a global parameter
        /// </summary>
        /// <param name="paramKeyType">Parameter type</param>
        /// <param name="value">Parameter value (integer)</param>
        public DataWriter AddUpdateGlobalParameter(GlobalParamKeyType paramKeyType, int value)
        {
            return AddUpdateGlobalParameter(paramKeyType, value.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Add or update a global parameter
        /// </summary>
        /// <param name="paramKeyType">Parameter type</param>
        /// <param name="value">Parameter value (double)</param>
        public DataWriter AddUpdateGlobalParameter(GlobalParamKeyType paramKeyType, double value)
        {
            return AddUpdateGlobalParameter(paramKeyType, value.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Add or update a global parameter
        /// </summary>
        /// <param name="paramKeyType">Parameter type</param>
        /// <param name="value">Parameter value (date)</param>
        public DataWriter AddUpdateGlobalParameter(GlobalParamKeyType paramKeyType, DateTime value)
        {
            return AddUpdateGlobalParameter(paramKeyType, UIMFDataUtilities.StandardizeDate(value));
        }


        /// <summary>
        /// Add or update a global parameter
        /// </summary>
        /// <param name="paramKeyType">Parameter type</param>
        /// <param name="value">Parameter value (string)</param>
        public DataWriter AddUpdateGlobalParameter(GlobalParamKeyType paramKeyType, string value)
        {
            try
            {

                if (!HasGlobalParamsTable)
                {
                    throw new Exception("The Global_Params table does not exist; " +
                                        "call method CreateTables before calling AddUpdateGlobalParameter");
                }

                if (mCreateLegacyParametersTables)
                {
                    if (!HasLegacyParameterTables)
                        throw new Exception(
                            "The Global_Parameters table does not exist (and mCreateLegacyParametersTables=true); " +
                            "call method CreateTables before calling AddUpdateGlobalParameter");
                }

                // SQLite does not have a merge statement
                // We therefore must first try an Update query
                // If no rows are matched, then run an insert query

                var globalParam = new GlobalParam(paramKeyType, value);

                mDbCommandUpdateGlobalParamValue.Parameters.Clear();

                mDbCommandUpdateGlobalParamValue.Parameters.Add(new SQLiteParameter("ParamID",
                                                                                    (int)globalParam.ParamType));
                mDbCommandUpdateGlobalParamValue.Parameters.Add(new SQLiteParameter("ParamValue", globalParam.Value));
                var updateCount = mDbCommandUpdateGlobalParamValue.ExecuteNonQuery();

                if (mCreateLegacyParametersTables)
                {
                    using (var dbCommand = mDbConnection.CreateCommand())
                    {
                        InsertLegacyGlobalParameter(dbCommand, paramKeyType, value);
                    }
                }

                if (updateCount == 0)
                {
                    mDbCommandInsertGlobalParamValue.Parameters.Clear();

                    mDbCommandInsertGlobalParamValue.Parameters.Add(new SQLiteParameter("ParamID",
                                                                                         (int)globalParam.ParamType));
                    mDbCommandInsertGlobalParamValue.Parameters.Add(new SQLiteParameter("ParamName", globalParam.Name));
                    mDbCommandInsertGlobalParamValue.Parameters.Add(new SQLiteParameter("ParamValue", globalParam.Value));
                    mDbCommandInsertGlobalParamValue.Parameters.Add(new SQLiteParameter("ParamDataType",
                                                                                         globalParam.DataType));
                    mDbCommandInsertGlobalParamValue.Parameters.Add(new SQLiteParameter("ParamDescription",
                                                                                         globalParam.Description));
                    mDbCommandInsertGlobalParamValue.ExecuteNonQuery();
                }

                mGlobalParameters.AddUpdateValue(paramKeyType, globalParam.Value);
            }
            catch (Exception ex)
            {
                CheckExceptionForIntermittentError(ex, "AddUpdateGlobalParameter");
                ReportError("Error adding/updating global parameter " + paramKeyType + ": " + ex.Message, ex);
                throw;
            }

            return this;
        }

        private void AddVersionInfo(Assembly entryAssembly = null)
        {
            var softwareName = "Unknown";
            var softwareVersion = new Version(0, 0, 0, 0);

            // Wrapping in a try/catch because NUnit breaks GetEntryAssembly().
            try
            {
                AssemblyName software;

                if (entryAssembly == null)
                {
                    software = Assembly.GetEntryAssembly().GetName();
                }
                else
                {
                    software = entryAssembly.GetName();
                }

                softwareName = software.Name;
                softwareVersion = software.Version;
            }
            catch
            {
                // Ignore errors here
            }

            AddVersionInfo(softwareName, softwareVersion);
        }

        /// <summary>
        /// Add version information to the version table
        /// </summary>
        /// <param name="softwareName">Name of the data acquisition software</param>
        /// <param name="softwareVersion">Version of the data acquisition software</param>
        public void AddVersionInfo(string softwareName, Version softwareVersion)
        {
            // File version is dependent on the major.minor version of the uimf library
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            var fileFormatVersion = version.ToString(2);

            using (var dbCommand = mDbConnection.CreateCommand())
            {
                dbCommand.CommandText = "INSERT INTO " + VERSION_INFO_TABLE + " "
                                        + "(File_Version, Calling_Assembly_Name, Calling_Assembly_Version) "
                                        + "VALUES(:Version, :SoftwareName, :SoftwareVersion);";

                dbCommand.Parameters.Add(new SQLiteParameter(":Version", fileFormatVersion));
                dbCommand.Parameters.Add(new SQLiteParameter(":SoftwareName", softwareName));
                dbCommand.Parameters.Add(new SQLiteParameter(":SoftwareVersion", softwareVersion));

                dbCommand.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Makes sure that all entries in the Frame_Params table have the given frame parameter defined
        /// </summary>
        /// <param name="paramKeyType"></param>
        /// <param name="paramValue"></param>
        /// <returns>The number of rows added (i.e. the number of frames that did not have the parameter)</returns>
        public int AssureAllFramesHaveFrameParam(FrameParamKeyType paramKeyType, string paramValue)
        {
            // Make sure the Frame_Param_Keys table contains key paramKeyType
            ValidateFrameParameterKey(paramKeyType);

            int rowsAdded;
            using (var dbCommand = mDbConnection.CreateCommand())
            {
                rowsAdded = AssureAllFramesHaveFrameParam(dbCommand, paramKeyType, paramValue);
            }

            return rowsAdded;
        }

        /// <summary>
        /// Makes sure that all entries in the Frame_Params table have the given frame parameter defined
        /// </summary>
        /// <param name="dbCommand"></param>
        /// <param name="paramKeyType"></param>
        /// <param name="paramValue"></param>
        /// <param name="frameNumStart">Optional: Starting frame number; ignored if frameNumEnd is 0 or negative</param>
        /// <param name="frameNumEnd">Optional: Ending frame number; ignored if frameNumEnd is 0 or negative</param>
        /// <returns>The number of rows added (i.e. the number of frames that did not have the parameter)</returns>
        private static int AssureAllFramesHaveFrameParam(
            IDbCommand dbCommand,
            FrameParamKeyType paramKeyType,
            string paramValue,
            int frameNumStart = 0,
            int frameNumEnd = 0)
        {

            if (string.IsNullOrEmpty(paramValue))
                paramValue = string.Empty;

            // This query finds the frame numbers that are missing the parameter, then performs the insert, all in one SQL statement
            dbCommand.CommandText =
                "INSERT INTO " + FRAME_PARAMS_TABLE + " (FrameNum, ParamID, ParamValue) " +
                "SELECT Distinct FrameNum, " + (int)paramKeyType + " AS ParamID, '" + paramValue + "' " +
                "FROM " + FRAME_PARAMS_TABLE + " " +
                "WHERE Not FrameNum In (SELECT FrameNum FROM " + FRAME_PARAMS_TABLE + " WHERE ParamID = " + (int)paramKeyType + ") ";

            if (frameNumEnd > 0)
            {
                dbCommand.CommandText += " AND FrameNum >= " + frameNumStart + " AND FrameNum <= " + frameNumEnd;
            }

            var rowsAdded = dbCommand.ExecuteNonQuery();

            return rowsAdded;

        }

        /// <summary>
        /// This function prints out a message to the console if we get a "disk image is malformed" exception
        /// </summary>
        /// <param name="ex"></param>
        /// <param name="callingFunction"></param>
        private void CheckExceptionForIntermittentError(Exception ex, string callingFunction)
        {
            if (ex.Message.Contains("disk image is malformed"))
                Console.WriteLine("Encountered 'disk image is malformed' in " + callingFunction);
        }

        /// <summary>
        /// This function will create tables that are bin centric (as opposed to scan centric) to allow querying of the data in 2 different ways.
        /// Bin centric data is important for data access speed in informed workflows.
        /// </summary>
        public void CreateBinCentricTables()
        {
            CreateBinCentricTables(string.Empty);
        }

        /// <summary>
        /// This function will create tables that are bin centric (as opposed to scan centric) to allow querying of the data in 2 different ways.
        /// Bin centric data is important for data access speed in informed quantitation workflows.
        /// </summary>
        /// <param name="workingDirectory">
        /// Path to the working directory in which a temporary SqLite database file should be created
        /// </param>
        public void CreateBinCentricTables(string workingDirectory)
        {
            if (TableExists("Bin_Intensities"))
                return;

            using (var uimfReader = new DataReader(mFilePath))
            {
                var binCentricTableCreator = new BinCentricTableCreation();
                binCentricTableCreator.CreateBinCentricTable(mDbConnection, uimfReader, workingDirectory);
            }
        }

        /// <summary>
        /// Remove the bin centric table and the related indices. Some UIMF write/update operations
        /// breaks the bin intensities table. Call this method after these operations to retain
        /// data integrity.
        /// </summary>
        public void RemoveBinCentricTables()
        {
            if (!TableExists("Bin_Intensities"))
                return;

            using (var dbCommand = mDbConnection.CreateCommand())
            {
                // Drop the table
                dbCommand.CommandText = "DROP TABLE Bin_Intensities);";
                dbCommand.ExecuteNonQuery();
            }

            FlushUimf(false);
        }

        /// <summary>
        /// Renumber frames so that the first frame is frame 1 and to assure that there are no gaps in frame numbers
        /// </summary>
        /// <remarks>This method is used by the UIMFDemultiplexer when the first frame to process is not frame 1</remarks>
        public void RenumberFrames()
        {

            try
            {
                var frameShifter = new FrameNumShifter(mDbConnection, HasLegacyParameterTables);
                frameShifter.FrameShiftEvent += FrameShifter_FrameShiftEvent;

                frameShifter.RenumberFrames();

                FlushUimf(true);
            }
            catch (Exception ex)
            {
                ReportError("Error renumbering frames: " + ex.Message, ex);
                throw;
            }

        }

        /// <summary>
        /// Create the Frame_Param_Keys and Frame_Params tables
        /// </summary>
        private void CreateFrameParamsTables(IDbCommand dbCommand)
        {

            if (HasFrameParamsTable && TableExists(FRAME_PARAM_KEYS_TABLE))
            {
                // The tables already exist
                return;
            }

            // Create table Frame_Param_Keys
            var lstFields = GetFrameParamKeysFields();
            dbCommand.CommandText = GetCreateTableSql(FRAME_PARAM_KEYS_TABLE, lstFields);
            dbCommand.ExecuteNonQuery();

            // Create table Frame_Params
            lstFields = GetFrameParamsFields();
            dbCommand.CommandText = GetCreateTableSql(FRAME_PARAMS_TABLE, lstFields);
            dbCommand.ExecuteNonQuery();

            // Create the unique index index on Frame_Param_Keys
            dbCommand.CommandText = "CREATE UNIQUE INDEX pk_index_FrameParamKeys on " + FRAME_PARAM_KEYS_TABLE + "(ParamID);";
            dbCommand.ExecuteNonQuery();

            // Create the unique index index on Frame_Params
            dbCommand.CommandText = "CREATE UNIQUE INDEX pk_index_FrameParams on " + FRAME_PARAMS_TABLE + "(FrameNum, ParamID);";
            dbCommand.ExecuteNonQuery();

            // Create a second index on Frame_Params, to allow for lookups by ParamID
            dbCommand.CommandText =
                "CREATE INDEX ix_index_FrameParams_By_ParamID on " + FRAME_PARAMS_TABLE + "(ParamID, FrameNum);";
            dbCommand.ExecuteNonQuery();

            // Create view V_Frame_Params
            dbCommand.CommandText =
                "CREATE VIEW V_Frame_Params AS " +
                "SELECT FP.FrameNum, FPK.ParamName, FP.ParamID, FP.ParamValue, FPK.ParamDescription, FPK.ParamDataType " +
                "FROM " + FRAME_PARAMS_TABLE + " FP INNER JOIN " +
                FRAME_PARAM_KEYS_TABLE + " FPK ON FP.ParamID = FPK.ParamID";
            dbCommand.ExecuteNonQuery();

            UpdateTableCheckedStatus(UIMFTableType.FrameParams, false);

        }

        private void CreateFrameScansTable(IDbCommand dbCommand, string dataType)
        {
            if (TableExists(FRAME_SCANS_TABLE))
            {
                // The tables already exist
                return;
            }

            // Create the Frame_Scans Table
            var lstFields = GetFrameScansFields(dataType);
            dbCommand.CommandText = GetCreateTableSql(FRAME_SCANS_TABLE, lstFields);
            dbCommand.ExecuteNonQuery();

            // Create the unique constraint indices
            // Although SQLite supports multi-column (compound) primary keys, the SQLite Manager plugin does not fully support them
            // thus, we'll use unique constraint indices to prevent duplicates

            // Create the unique index on Frame_Scans
            dbCommand.CommandText = "CREATE UNIQUE INDEX pk_index_FrameScans on " + FRAME_SCANS_TABLE + "(FrameNum, ScanNum);";
            dbCommand.ExecuteNonQuery();


        }

        /// <summary>
        /// Create the Global_Params table
        /// </summary>
        private void CreateGlobalParamsTable(IDbCommand dbCommand)
        {
            if (HasGlobalParamsTable)
            {
                // The table already exists
                return;
            }

            // Create the Global_Params Table
            var lstFields = GetGlobalParamsFields();
            dbCommand.CommandText = GetCreateTableSql(GLOBAL_PARAMS_TABLE, lstFields);
            dbCommand.ExecuteNonQuery();

            // Create the unique index index on Global_Params
            dbCommand.CommandText = "CREATE UNIQUE INDEX pk_index_GlobalParams on " + GLOBAL_PARAMS_TABLE + "(ParamID);";
            dbCommand.ExecuteNonQuery();

            UpdateTableCheckedStatus(UIMFTableType.GlobalParams, false);

        }

        private void CreateVersionInfoTable(IDbCommand dbCommand, Assembly entryAssembly)
        {
            if (HasVersionInfoTable)
            {
                // The table already exists
                return;
            }

            // Create the Version_Info Table
            var lstFields = GetVersionInfoFields();
            dbCommand.CommandText = GetCreateTableSql(VERSION_INFO_TABLE, lstFields);
            dbCommand.ExecuteNonQuery();

            // Create the unique index index on Version_Info
            dbCommand.CommandText = "CREATE UNIQUE INDEX pk_index_VersionInfo on " + VERSION_INFO_TABLE + "(Version_ID);";
            dbCommand.ExecuteNonQuery();

            UpdateTableCheckedStatus(UIMFTableType.VersionInfo, false);

            AddVersionInfo(entryAssembly);
        }

        /// <summary>
        /// Create legacy parameter tables (Global_Parameters and Frame_Parameters)
        /// </summary>
        /// <param name="dbCommand"></param>
        private void CreateLegacyParameterTables(IDbCommand dbCommand)
        {
            if (!TableExists(GLOBAL_PARAMETERS_TABLE))
            {
                // Create the Global_Parameters Table
                var lstFields = GetGlobalParametersFields();
                dbCommand.CommandText = GetCreateTableSql(GLOBAL_PARAMETERS_TABLE, lstFields);
                dbCommand.ExecuteNonQuery();

            }

            if (!TableExists(FRAME_PARAMETERS_TABLE))
            {
                // Create the Frame_parameters Table
                var lstFields = GetFrameParametersFields();
                dbCommand.CommandText = GetCreateTableSql(FRAME_PARAMETERS_TABLE, lstFields);
                dbCommand.ExecuteNonQuery();
            }

            UpdateTableCheckedStatus(UIMFTableType.LegacyGlobalParameters, false);
        }

        /// <summary>
        /// Create the table structure within a UIMF file
        /// </summary>
        /// <param name="dataType">Data type of intensity in the Frame_Scans table: double, float, short, or int </param>
        /// <param name="entryAssembly">Entry assembly, used when adding a line to the Version_Info table</param>
        /// <remarks>
        /// This must be called after opening a new file to create the default tables that are required for IMS data.
        /// </remarks>
        public void CreateTables(string dataType = "int", Assembly entryAssembly = null)
        {
            // Detailed information on columns is at
            // https://prismwiki.pnl.gov/wiki/IMS_Data_Processing

            using (var dbCommand = mDbConnection.CreateCommand())
            {

                // Create the Global_Params Table
                CreateGlobalParamsTable(dbCommand);

                // Create the Frame_Params tables
                CreateFrameParamsTables(dbCommand);

                // Create the Frame_Scans table
                CreateFrameScansTable(dbCommand, dataType);

                // Create the Version_Info table
                CreateVersionInfoTable(dbCommand, entryAssembly);

                if (mCreateLegacyParametersTables)
                {
                    CreateLegacyParameterTables(dbCommand);
                }
            }

            FlushUimf();
        }

        private void DecrementFrameCount(SQLiteCommand dbCommand, int frameCountToRemove = 1)
        {
            if (frameCountToRemove < 1)
                return;

            var numFrames = 0;

            dbCommand.CommandText = "SELECT ParamValue AS NumFrames From " + GLOBAL_PARAMS_TABLE + " WHERE ParamID=" + (int)GlobalParamKeyType.NumFrames;
            using (var reader = dbCommand.ExecuteReader())
            {
                if (reader.Read())
                {
                    var value = reader.GetString(0);
                    if (int.TryParse(value, out numFrames))
                    {
                        numFrames -= frameCountToRemove;
                        if (numFrames < 0)
                            numFrames = 0;
                    }
                }
            }

            AddUpdateGlobalParameter(GlobalParamKeyType.NumFrames, numFrames.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Deletes the scans for all frames in the file.  In addition, updates the Scans column to 0 in Frame_Params for all frames.
        /// </summary>
        /// <param name="frameType">
        /// </param>
        /// <param name="updateScanCountInFrameParams">
        /// If true, then will update the Scans column to be 0 for the deleted frames
        /// </param>
        /// <param name="bShrinkDatabaseAfterDelete">
        /// </param>
        /// <remarks>
        /// As an alternative to using this function, use CloneUIMF() in the DataReader class
        /// </remarks>
        public void DeleteAllFrameScans(int frameType, bool updateScanCountInFrameParams, bool bShrinkDatabaseAfterDelete)
        {
            using (var dbCommand = mDbConnection.CreateCommand())
            {

                dbCommand.CommandText = "DELETE FROM " + FRAME_SCANS_TABLE + " " +
                                        "WHERE FrameNum IN " +
                                        "   (SELECT DISTINCT FrameNum " +
                                        "    FROM " + FRAME_PARAMS_TABLE +
                                        "    WHERE ParamID = " + (int)FrameParamKeyType.FrameType + " AND" +
                                        "          ParamValue = " + frameType + ");";
                dbCommand.ExecuteNonQuery();

                if (updateScanCountInFrameParams)
                {
                    dbCommand.CommandText = "UPDATE " + FRAME_PARAMS_TABLE + " " +
                                            "SET ParamValue = '0' " +
                                            "WHERE ParamID = " + (int)FrameParamKeyType.Scans +
                                            "  AND FrameNum IN " +
                                            "   (SELECT DISTINCT FrameNum" +
                                            "    FROM " + FRAME_PARAMS_TABLE +
                                            "    WHERE ParamID = " + (int)FrameParamKeyType.FrameType + " AND" +
                                            "          ParamValue = " + frameType + ");";
                    dbCommand.ExecuteNonQuery();
                }

                // Commit the currently open transaction
                TransactionCommit();
                System.Threading.Thread.Sleep(100);

                if (bShrinkDatabaseAfterDelete)
                {
                    dbCommand.CommandText = "VACUUM;";
                    dbCommand.ExecuteNonQuery();
                }

                // Open a new transaction
                TransactionBegin();

            }
        }

        /// <summary>
        /// Deletes the frame from the Frame_Params table and from the Frame_Scans table
        /// </summary>
        /// <param name="frameNum">
        /// </param>
        /// <param name="updateGlobalParameters">
        /// If true, then decrements the NumFrames value in the Global_Params table
        /// </param>
        public void DeleteFrame(int frameNum, bool updateGlobalParameters)
        {
            using (var dbCommand = mDbConnection.CreateCommand())
            {

                dbCommand.CommandText = "DELETE FROM " + FRAME_SCANS_TABLE + " WHERE FrameNum = " + frameNum + "; ";
                dbCommand.ExecuteNonQuery();

                dbCommand.CommandText = "DELETE FROM " + FRAME_PARAMS_TABLE + " WHERE FrameNum = " + frameNum + "; ";
                dbCommand.ExecuteNonQuery();

                if (updateGlobalParameters)
                {
                    DecrementFrameCount(dbCommand);
                }
            }

            FlushUimf(false);
        }

        /// <summary>
        /// Deletes all of the scans for the specified frame
        /// </summary>
        /// <param name="frameNum">
        /// The frame number to delete
        /// </param>
        /// <param name="updateScanCountInFrameParams">
        /// If true, then will update the Scans column to be 0 for the deleted frames
        /// </param>
        public void DeleteFrameScans(int frameNum, bool updateScanCountInFrameParams)
        {
            using (var dbCommand = mDbConnection.CreateCommand())
            {

                dbCommand.CommandText = "DELETE FROM " + FRAME_SCANS_TABLE + " WHERE FrameNum = " + frameNum + "; ";
                dbCommand.ExecuteNonQuery();

                if (updateScanCountInFrameParams)
                {
                    dbCommand.CommandText = "UPDATE " + FRAME_PARAMS_TABLE + " " +
                                            "SET ParamValue = '0' " +
                                            "WHERE FrameNum = " + frameNum +
                                             " AND ParamID = " + (int)FrameParamKeyType.Scans + ";";
                    dbCommand.ExecuteNonQuery();

                    if (HasLegacyParameterTables)
                    {
                        dbCommand.CommandText = "UPDATE " + FRAME_PARAMETERS_TABLE + " " +
                                                "SET Scans = 0 " +
                                                "WHERE FrameNum = " + frameNum + ";";
                        dbCommand.ExecuteNonQuery();
                    }
                }

            }

            FlushUimf(false);
        }

        /// <summary>
        /// Delete the given frames from the UIMF file.
        /// </summary>
        /// <param name="frameNums">
        /// </param>
        /// <param name="updateGlobalParameters">
        /// </param>
        public void DeleteFrames(List<int> frameNums, bool updateGlobalParameters)
        {
            // Construct a comma-separated list of frame numbers
            var sFrameList = string.Join(",", frameNums);

            using (var dbCommand = mDbConnection.CreateCommand())
            {

                dbCommand.CommandText = "DELETE FROM " + FRAME_SCANS_TABLE + " WHERE FrameNum IN (" + sFrameList + "); ";
                dbCommand.ExecuteNonQuery();

                dbCommand.CommandText = "DELETE FROM " + FRAME_PARAMS_TABLE + " WHERE FrameNum IN (" + sFrameList + "); ";
                dbCommand.ExecuteNonQuery();

                if (HasLegacyParameterTables)
                {
                    dbCommand.CommandText = "DELETE FROM " + FRAME_PARAMETERS_TABLE + " WHERE FrameNum IN (" + sFrameList + "); ";
                    dbCommand.ExecuteNonQuery();
                }

                if (updateGlobalParameters)
                {
                    DecrementFrameCount(dbCommand, frameNums.Count);
                }

            }

            FlushUimf(true);
        }

        /// <summary>
        /// Dispose of any system resources
        /// </summary>
        /// <param name="disposing">
        /// True when disposing
        /// </param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (mDbConnection != null)
                {
                    TransactionCommit();

                    DisposeCommand(mDbCommandInsertFrameParamKey);
                    DisposeCommand(mDbCommandInsertFrameParamValue);
                    DisposeCommand(mDbCommandUpdateFrameParamValue);
                    DisposeCommand(mDbCommandInsertLegacyFrameParameterRow);
                    DisposeCommand(mDbCommandInsertGlobalParamValue);
                    DisposeCommand(mDbCommandUpdateGlobalParamValue);
                    DisposeCommand(mDbCommandInsertScan);
                }
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// Commits the currently open transaction, then starts a new one
        /// </summary>
        /// <remarks>
        /// Note that a transaction is started when the UIMF file is opened, then committed when the class is disposed
        /// </remarks>
        public void FlushUimf()
        {
            FlushUimf(true);
        }

        /// <summary>
        /// Commits the currently open transaction, then starts a new one
        /// </summary>
        /// <param name="forceFlush">True to force a flush; otherwise, will only flush if the last one was 5 or more seconds ago</param>
        /// <remarks>
        /// Note that a transaction is started when the UIMF file is opened, then committed when the class is disposed
        /// </remarks>
        public void FlushUimf(bool forceFlush)
        {
            if (forceFlush | DateTime.UtcNow.Subtract(mLastFlush).TotalSeconds >= MINIMUM_FLUSH_INTERVAL_SECONDS)
            {
                mLastFlush = DateTime.UtcNow;

                try
                {
                    TransactionCommit();
                }
                catch (Exception ex)
                {
                    CheckExceptionForIntermittentError(ex, "FlushUimf, TransactionCommit");
                    throw;
                }

                // We were randomly getting error "database disk image is malformed"
                // This sleep appears helps fix things, but not 100%, especially if writing to data over a network share
                System.Threading.Thread.Sleep(100);

                try
                {
                    TransactionBegin();
                }
                catch (Exception ex)
                {
                    CheckExceptionForIntermittentError(ex, "FlushUimf, TransactionBegin");
                    throw;
                }
            }
        }

        /// <summary>
        /// Method to insert details related to each IMS frame
        /// </summary>
        /// <param name="frameParameters">
        /// </param>
        [Obsolete("Use AddUpdateFrameParameter or use InsertFrame with 'Dictionary<FrameParamKeyType, dynamic> frameParameters'")]
        public void InsertFrame(FrameParameters frameParameters)
        {
            var frameParams = FrameParamUtilities.ConvertFrameParameters(frameParameters);

            InsertFrame(frameParameters.FrameNum, frameParams);
        }

        /// <summary>
        /// Method to insert details related to each IMS frame
        /// </summary>
        /// <param name="frameNum">Frame number</param>
        /// <param name="frameParameters">FrameParams object</param>
        public DataWriter InsertFrame(int frameNum, FrameParams frameParameters)
        {
            var frameParamsLite = frameParameters.Values.ToDictionary(frameParam => frameParam.Key, frameParam => frameParam.Value.Value);
            return InsertFrame(frameNum, frameParamsLite);
        }

        /// <summary>
        /// Method to insert details related to each IMS frame
        /// </summary>
        /// <param name="frameNum">Frame number</param>
        /// <param name="frameParameters">Frame parameters dictionary</param>
        public DataWriter InsertFrame(int frameNum, Dictionary<FrameParamKeyType, dynamic> frameParameters)
        {
            // Make sure the previous frame's data is committed to the database
            // However, only flush the data every MINIMUM_FLUSH_INTERVAL_SECONDS
            FlushUimf(false);

            if (!HasFrameParamsTable)
                throw new Exception("The Frame_Params table does not exist; call method CreateTables before calling InsertFrame");

            if (mCreateLegacyParametersTables)
            {
                if (!HasLegacyParameterTables)
                    throw new Exception(
                        "The Frame_Parameters table does not exist (and mCreateLegacyParametersTables=true); " +
                        "call method CreateTables before calling InsertFrame");
            }

            // Make sure the Frame_Param_Keys table has the required keys
            ValidateFrameParameterKeys(frameParameters.Keys.ToList());

            // Store each of the FrameParameters values as FrameNum, ParamID, Value entries

            try
            {
                foreach (var paramValue in frameParameters)
                {
                    var value = paramValue.Value;
                    // double.NaN and float.NaN: Make sure they are output as 'NaN'; without this override, they are output as 'NULL'
                    if ((value is double || value is float) && double.IsNaN(value))
                    {
                        value = "NaN";
                    }
                    mDbCommandInsertFrameParamValue.Parameters.Clear();
                    mDbCommandInsertFrameParamValue.Parameters.Add(new SQLiteParameter("FrameNum", frameNum));
                    mDbCommandInsertFrameParamValue.Parameters.Add(new SQLiteParameter("ParamID", (int)paramValue.Key));
                    mDbCommandInsertFrameParamValue.Parameters.Add(new SQLiteParameter("ParamValue", value));
                    mDbCommandInsertFrameParamValue.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                CheckExceptionForIntermittentError(ex, "InsertFrame");
                throw;
            }

            if (mCreateLegacyParametersTables)
            {
                InsertLegacyFrameParams(frameNum, frameParameters);
            }

            return this;
        }

        /// <summary>
        /// Method to enter the details of the global parameters for the experiment
        /// </summary>
        /// <param name="globalParameters">
        /// </param>
        [Obsolete("Use AddUpdateGlobalParameter or use InsertGlobal with 'GlobalParams globalParameters'")]
        public void InsertGlobal(GlobalParameters globalParameters)
        {
            var globalParams = GlobalParamUtilities.ConvertGlobalParameters(globalParameters);
            foreach (var globalParam in globalParams)
            {
                AddUpdateGlobalParameter(globalParam.Key, globalParam.Value);
            }
        }

        /// <summary>
        /// Method to enter the details of the global parameters for the experiment
        /// </summary>
        /// <param name="globalParameters">
        /// </param>
        public DataWriter InsertGlobal(GlobalParams globalParameters)
        {
            foreach (var globalParam in globalParameters.Values)
            {
                var paramEntry = globalParam.Value;
                AddUpdateGlobalParameter(paramEntry.ParamType, paramEntry.Value);
            }

            return this;
        }

        /// <summary>
        /// Insert a row into the legacy Frame_Parameters table
        /// </summary>
        /// <param name="frameParameters">
        /// </param>
#pragma warning disable 612, 618
        private void InitializeFrameParametersRow(FrameParameters frameParameters)
#pragma warning restore 612, 618
        {
            if (mFrameNumsInLegacyFrameParametersTable.Contains(frameParameters.FrameNum))
            {
                // Row already exists; don't try to re-add it
                return;
            }

            // Make sure the Frame_Parameters table has the Decoded column
            ValidateLegacyDecodedColumnExists();

            mDbCommandInsertLegacyFrameParameterRow.Parameters.Clear();

            // Frame number (primary key)
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":FrameNum", frameParameters.FrameNum));

            // Start time of frame, in minutes
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":StartTime", frameParameters.StartTime));

            // Duration of frame, in seconds
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":Duration", frameParameters.Duration));

            // Number of collected and summed acquisitions in a frame
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(
                new SQLiteParameter(":Accumulations", frameParameters.Accumulations));

            // Bitmap: 0=MS (Legacy); 1=MS (Regular); 2=MS/MS (Frag); 3=Calibration; 4=Prescan
            // See also the FrameType enum in the UIMFData class
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":FrameType", (int)frameParameters.FrameType));

            // Number of TOF scans
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":Scans", frameParameters.Scans));

            // IMFProfile Name; this stores the name of the sequence used to encode the data when acquiring data multiplexed
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":IMFProfile", frameParameters.IMFProfile));

            // Number of TOF Losses
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":TOFLosses", frameParameters.TOFLosses));

            // Average time between TOF trigger pulses
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(
                new SQLiteParameter(":AverageTOFLength", frameParameters.AverageTOFLength));

            // Value of k0
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(
                new SQLiteParameter(":CalibrationSlope", frameParameters.CalibrationSlope));

            // Value of t0
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(
                new SQLiteParameter(":CalibrationIntercept", frameParameters.CalibrationIntercept));

            // These six parameters below are coefficients for residual mass error correction
            //   ResidualMassError=a2t+b2t^3+c2t^5+d2t^7+e2t^9+f2t^11
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":a2", frameParameters.a2));
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":b2", frameParameters.b2));
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":c2", frameParameters.c2));
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":d2", frameParameters.d2));
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":e2", frameParameters.e2));
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":f2", frameParameters.f2));

            // Ambient temperature
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":Temperature", frameParameters.Temperature));

            // Voltage setting in the IMS system
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":voltHVRack1", frameParameters.voltHVRack1));

            // Voltage setting in the IMS system
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":voltHVRack2", frameParameters.voltHVRack2));

            // Voltage setting in the IMS system
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":voltHVRack3", frameParameters.voltHVRack3));

            // Voltage setting in the IMS system
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":voltHVRack4", frameParameters.voltHVRack4));

            // Capillary Inlet Voltage
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":voltCapInlet", frameParameters.voltCapInlet));

            // HPF In Voltage
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(
                new SQLiteParameter(":voltEntranceHPFIn", frameParameters.voltEntranceHPFIn));

            // HPF Out Voltage
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(
                new SQLiteParameter(":voltEntranceHPFOut", frameParameters.voltEntranceHPFOut));

            // Cond Limit Voltage
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(
                new SQLiteParameter(":voltEntranceCondLmt", frameParameters.voltEntranceCondLmt));

            // Trap Out Voltage
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":voltTrapOut", frameParameters.voltTrapOut));

            // Trap In Voltage
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":voltTrapIn", frameParameters.voltTrapIn));

            // Jet Disruptor Voltage
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":voltJetDist", frameParameters.voltJetDist));

            // Fragmentation Quadrupole Voltage
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":voltQuad1", frameParameters.voltQuad1));

            // Fragmentation Conductance Voltage
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":voltCond1", frameParameters.voltCond1));

            // Fragmentation Quadrupole Voltage
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":voltQuad2", frameParameters.voltQuad2));

            // Fragmentation Conductance Voltage
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":voltCond2", frameParameters.voltCond2));

            // IMS Out Voltage
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":voltIMSOut", frameParameters.voltIMSOut));

            // HPF In Voltage
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(
                new SQLiteParameter(":voltExitHPFIn", frameParameters.voltExitHPFIn));

            // HPF Out Voltage
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(
                new SQLiteParameter(":voltExitHPFOut", frameParameters.voltExitHPFOut));

            // Cond Limit Voltage
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(
                new SQLiteParameter(":voltExitCondLmt", frameParameters.voltExitCondLmt));

            // Pressure at front of Drift Tube
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(
                new SQLiteParameter(":PressureFront", frameParameters.PressureFront));

            // Pressure at back of Drift Tube
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":PressureBack", frameParameters.PressureBack));

            // Determines original size of bit sequence
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":MPBitOrder", frameParameters.MPBitOrder));

            // Voltage profile used in fragmentation
            // Convert the array of doubles to an array of bytes
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(
                new SQLiteParameter(":FragmentationProfile", FrameParamUtilities.ConvertToBlob(frameParameters.FragmentationProfile)));

            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(
                new SQLiteParameter(":HPPressure", frameParameters.HighPressureFunnelPressure));
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(
                new SQLiteParameter(":IPTrapPressure", frameParameters.IonFunnelTrapPressure));
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(
                new SQLiteParameter(":RIFunnelPressure", frameParameters.RearIonFunnelPressure));
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(
                new SQLiteParameter(":QuadPressure", frameParameters.QuadrupolePressure));

            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":ESIVoltage", frameParameters.ESIVoltage));

            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":FloatVoltage", frameParameters.FloatVoltage));

            // Set to 1 after a frame has been calibrated
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(
                new SQLiteParameter(":CalibrationDone", frameParameters.CalibrationDone));

            // Set to 1 after a frame has been decoded (added June 27, 2011)
            mDbCommandInsertLegacyFrameParameterRow.Parameters.Add(new SQLiteParameter(":Decoded", frameParameters.Decoded));

            mDbCommandInsertLegacyFrameParameterRow.ExecuteNonQuery();

            mFrameNumsInLegacyFrameParametersTable.Add(frameParameters.FrameNum);

        }


        /// <summary>
        /// Insert a row into the legacy Global_Parameters table
        /// </summary>
        /// <param name="globalParameters">
        /// </param>
#pragma warning disable 612, 618
        private void InitializeGlobalParametersRow(GlobalParameters globalParameters)
#pragma warning restore 612, 618
        {
            var dbCommand = mDbConnection.CreateCommand();

            dbCommand.CommandText = "INSERT INTO " + GLOBAL_PARAMETERS_TABLE + " "
                + "(DateStarted, NumFrames, TimeOffset, BinWidth, Bins, TOFCorrectionTime, FrameDataBlobVersion, ScanDataBlobVersion, "
                + "TOFIntensityType, DatasetType, Prescan_TOFPulses, Prescan_Accumulations, Prescan_TICThreshold, Prescan_Continuous, Prescan_Profile, Instrument_name) "
                + "VALUES(:DateStarted, :NumFrames, :TimeOffset, :BinWidth, :Bins, :TOFCorrectionTime, :FrameDataBlobVersion, :ScanDataBlobVersion, "
                + ":TOFIntensityType, :DatasetType, :Prescan_TOFPulses, :Prescan_Accumulations, :Prescan_TICThreshold, :Prescan_Continuous, :Prescan_Profile, :Instrument_name);";

            dbCommand.Parameters.Add(new SQLiteParameter(":DateStarted", globalParameters.DateStarted));
            dbCommand.Parameters.Add(new SQLiteParameter(":NumFrames", globalParameters.NumFrames));
            dbCommand.Parameters.Add(new SQLiteParameter(":TimeOffset", globalParameters.TimeOffset));
            dbCommand.Parameters.Add(new SQLiteParameter(":BinWidth", globalParameters.BinWidth));
            dbCommand.Parameters.Add(new SQLiteParameter(":Bins", globalParameters.Bins));
            dbCommand.Parameters.Add(new SQLiteParameter(":TOFCorrectionTime", globalParameters.TOFCorrectionTime));
            dbCommand.Parameters.Add(new SQLiteParameter(":FrameDataBlobVersion", globalParameters.FrameDataBlobVersion));
            dbCommand.Parameters.Add(new SQLiteParameter(":ScanDataBlobVersion", globalParameters.ScanDataBlobVersion));
            dbCommand.Parameters.Add(new SQLiteParameter(":TOFIntensityType", globalParameters.TOFIntensityType));
            dbCommand.Parameters.Add(new SQLiteParameter(":DatasetType", globalParameters.DatasetType));
            dbCommand.Parameters.Add(new SQLiteParameter(":Prescan_TOFPulses", globalParameters.Prescan_TOFPulses));
            dbCommand.Parameters.Add(new SQLiteParameter(":Prescan_Accumulations", globalParameters.Prescan_Accumulations));
            dbCommand.Parameters.Add(new SQLiteParameter(":Prescan_TICThreshold", globalParameters.Prescan_TICThreshold));
            dbCommand.Parameters.Add(new SQLiteParameter(":Prescan_Continuous", globalParameters.Prescan_Continuous));
            dbCommand.Parameters.Add(new SQLiteParameter(":Prescan_Profile", globalParameters.Prescan_Profile));
            dbCommand.Parameters.Add(new SQLiteParameter(":Instrument_name", globalParameters.InstrumentName));

            dbCommand.ExecuteNonQuery();

        }

        /// <summary>
        /// Write out the compressed intensity data to the UIMF file
        /// </summary>
        /// <param name="frameParameters">Legacy frame parameters</param>
        /// <param name="scanNum">scan number</param>
        /// <param name="binWidth">Bin width (in ns)</param>
        /// <param name="indexOfMaxIntensity">index of maximum intensity (for determining the base peak m/z)</param>
        /// <param name="nonZeroCount">Count of non-zero values</param>
        /// <param name="bpi">Base peak intensity (intensity of bin indexOfMaxIntensity)</param>
        /// <param name="tic">Total ion intensity</param>
        /// <param name="spectra">Mass spectra intensities</param>
        [Obsolete("Use InsertScanStoreBytes that accepts a FrameParams object")]
        // ReSharper disable once UnusedMember.Local
        private void InsertScanStoreBytes(
            FrameParameters frameParameters,
            int scanNum,
            double binWidth,
            int indexOfMaxIntensity,
            int nonZeroCount,
            double bpi,
            Int64 tic,
            byte[] spectra)
        {
            if (nonZeroCount <= 0)
                return;

            var bpiMz = ConvertBinToMz(indexOfMaxIntensity, binWidth, frameParameters);

            // Insert records.
            ValidateFrameScansExists("InsertScanStoreBytes");

            InsertScanAddParameters(frameParameters.FrameNum, scanNum, nonZeroCount, (int)bpi, bpiMz, tic, spectra);
            mDbCommandInsertScan.ExecuteNonQuery();

        }

        /// <summary>
        /// Write out the compressed intensity data to the UIMF file
        /// </summary>
        /// <param name="frameNumber">Frame number</param>
        /// <param name="frameParameters">FrameParams</param>
        /// <param name="scanNum">
        /// Scan number
        /// Traditionally the first scan in a frame has been scan 0, but we switched to start with Scan 1 in 2015.
        /// </param>
        /// <param name="binWidth">Bin width (in ns)</param>
        /// <param name="indexOfMaxIntensity">index of maximum intensity (for determining the base peak m/z)</param>
        /// <param name="nonZeroCount">Count of non-zero values</param>
        /// <param name="bpi">Base peak intensity (intensity of bin indexOfMaxIntensity)</param>
        /// <param name="tic">Total ion intensity</param>
        /// <param name="spectra">Mass spectra intensities</param>
        private void InsertScanStoreBytes(
            int frameNumber,
            FrameParams frameParameters,
            int scanNum,
            double binWidth,
            int indexOfMaxIntensity,
            int nonZeroCount,
            int bpi,
            long tic,
            byte[] spectra)
        {
            if (nonZeroCount <= 0)
                return;

            var bpiMz = ConvertBinToMz(indexOfMaxIntensity, binWidth, frameParameters);

            // Insert records.
            ValidateFrameScansExists("InsertScanStoreBytes");

            InsertScanAddParameters(frameNumber, scanNum, nonZeroCount, bpi, bpiMz, tic, spectra);
            mDbCommandInsertScan.ExecuteNonQuery();

        }

        /// <summary>Insert a new scan using an array of intensities (as integers) along with binWidth</summary>
        /// <param name="frameNumber">Frame Number</param>
        /// <param name="frameParameters">Frame parameters</param>
        /// <param name="scanNum">
        /// Scan number
        /// Traditionally the first scan in a frame has been scan 0, but we switched to start with Scan 1 in 2015.
        /// </param>
        /// <param name="intensities">Array of intensities, including all zeros</param>
        /// <param name="binWidth">Bin width (in nanoseconds, used to compute m/z value of the BPI data point)</param>
        /// <returns>Number of non-zero data points</returns>
        /// <remarks>The intensities array should contain an intensity for every bin, including all of the zeroes</remarks>
        public void InsertScan(
            int frameNumber,
            FrameParams frameParameters,
            int scanNum,
            IList<int> intensities,
            double binWidth)
        {
            InsertScan(frameNumber, frameParameters, scanNum, intensities, binWidth, out _);
        }

        /// <summary>Insert a new scan using an array of intensities (as integers) along with binWidth</summary>
        /// <param name="frameNumber">Frame Number</param>
        /// <param name="frameParameters">Frame parameters</param>
        /// <param name="scanNum">
        /// Scan number
        /// Traditionally the first scan in a frame has been scan 0, but we switched to start with Scan 1 in 2015.
        /// </param>
        /// <param name="intensities">Array of intensities, including all zeros</param>
        /// <param name="binWidth">Bin width (in nanoseconds, used to compute m/z value of the BPI data point)</param>
        /// <param name="nonZeroCount">Number of non-zero data points (output)</param>
        /// <remarks>The intensities array should contain an intensity for every bin, including all of the zeroes</remarks>
        public void InsertScan(
            int frameNumber,
            FrameParams frameParameters,
            int scanNum,
            IList<int> intensities,
            double binWidth,
            out int nonZeroCount)
        {

            if (frameParameters == null)
                throw new ArgumentNullException(nameof(frameParameters));

            if (mGlobalParameters.IsPpmBinBased)
                throw new InvalidOperationException("You cannot call InsertScan when the InstrumentClass is ppm bin-based; instead use InsertScanPpmBinBased");

            // Make sure intensities.Count is not greater than the number of bins tracked by the global parameters
            // However, allow it to be one size larger because GetSpectrumAsBins pads the intensities array with an extra value for compatibility with older UIMF files
            if (intensities.Count > mGlobalParameters.Bins + 1)
            {
                throw new Exception("Intensity list for frame " + frameNumber + ", scan " + scanNum +
                                    " has more entries than the number of bins defined in the global parameters" +
                                    " (" + mGlobalParameters.Bins + ")");

                // Future possibility: silently auto-change the Bins value
                // AddUpdateGlobalParameter(GlobalParamKeyType.Bins, maxBin);
            }

            // Convert the intensities array into a zero length encoded byte array, stored in variable spectrum
            nonZeroCount = IntensityConverterCLZF.Compress(intensities.ToList(), out var spectrum, out var tic, out var bpi, out var indexOfMaxIntensity);

            InsertScanStoreBytes(frameNumber, frameParameters, scanNum, binWidth, indexOfMaxIntensity, nonZeroCount, (int)bpi, (long)tic, spectrum);

        }

        /// <summary>
        /// This method takes in a list of intensity information by bin and converts the data to a run length encoded array
        /// which is later compressed at the byte level for reduced size
        /// </summary>
        /// <param name="frameNumber">Frame number</param>
        /// <param name="frameParameters">FrameParams</param>
        /// <param name="scanNum">Scan number</param>
        /// <param name="binToIntensityMap">Keys are bin numbers and values are intensity values; intensity values are assumed to all be non-zero</param>
        /// <param name="binWidth">Bin width (in ns)</param>
        /// <param name="timeOffset">Time offset</param>
        /// <returns>Non-zero data count<see cref="int"/></returns>
        /// <remarks>Assumes that all data in binToIntensityMap has positive (non-zero) intensities</remarks>
        [Obsolete("Use the version of InsertScan that takes a list of Tuples")]
        public int InsertScan(
            int frameNumber,
            FrameParams frameParameters,
            int scanNum,
            IList<KeyValuePair<int, int>> binToIntensityMap,
            double binWidth,
            int timeOffset)
        {

            var binToIntensityMapCopy = new List<Tuple<int, int>>(binToIntensityMap.Count);
            binToIntensityMapCopy.AddRange(binToIntensityMap.Select(item => new Tuple<int, int>(item.Key, item.Value)));

            return InsertScan(frameNumber, frameParameters, scanNum, binToIntensityMapCopy, binWidth, timeOffset);
        }

        /// <summary>
        /// This method takes in a list of intensity information by bin and converts the data to a run length encoded array
        /// which is later compressed at the byte level for reduced size
        /// </summary>
        /// <param name="frameNumber">Frame number</param>
        /// <param name="frameParameters">FrameParams</param>
        /// <param name="scanNum">Scan number</param>
        /// <param name="binToIntensityMap">Keys are bin numbers and values are intensity values; intensity values are assumed to all be non-zero</param>
        /// <param name="binWidth">Bin width (in ns)</param>
        /// <param name="timeOffset">Time offset</param>
        /// <returns>Non-zero data count<see cref="int"/></returns>
        /// <remarks>Assumes that all data in binToIntensityMap has positive (non-zero) intensities</remarks>
        public int InsertScan(
            int frameNumber,
            FrameParams frameParameters,
            int scanNum,
            IList<Tuple<int, int>> binToIntensityMap,
            double binWidth,
            int timeOffset)
        {

            if (frameParameters == null)
                throw new ArgumentNullException(nameof(frameParameters));

            if (binToIntensityMap == null)
                throw new ArgumentNullException(nameof(binToIntensityMap), "binToIntensityMap cannot be null");

            if (mGlobalParameters.IsPpmBinBased)
                throw new InvalidOperationException("You cannot call InsertScan when the InstrumentClass is ppm bin-based; instead use InsertScanPpmBinBased");

            if (binToIntensityMap.Count == 0)
            {
                return 0;
            }

            // Assure that binToIntensityMap does not have any intensities of 0 (since their presence messes up the encoding)
            if ((from item in binToIntensityMap where item.Item2 == 0 select item).Any())
            {
                throw new ArgumentException("Intensity value of 0 found in binToIntensityMap", nameof(binToIntensityMap));
            }

            var maxBin = (from item in binToIntensityMap select item.Item1).Max();

            // Make sure intensities.Count is not greater than the number of bins tracked by the global parameters
            // However, allow it to be one size larger because GetSpectrumAsBins pads the intensities array with an extra value for compatibility with older UIMF files
            if (maxBin > mGlobalParameters.Bins + 1)
            {
                throw new Exception("Intensity list for frame " + frameNumber + ", scan " + scanNum +
                                    " has more entries than the number of bins defined in the global parameters" +
                                    " (" + mGlobalParameters.Bins + ")");

                // Future possibility: silently auto-change the Bins value
                // AddUpdateGlobalParameter(GlobalParamKeyType.Bins, maxBin);
            }

            var nonZeroCount = IntensityBinConverterInt32.Encode(binToIntensityMap, timeOffset, out var spectrum, out var tic, out var bpi, out var binNumberMaxIntensity);

            InsertScanStoreBytes(frameNumber, frameParameters, scanNum, binWidth, binNumberMaxIntensity, nonZeroCount, (int)bpi, (long)tic, spectrum);

            return nonZeroCount;

        }

        /// <summary>
        /// Update the slope and intercept for all frames
        /// </summary>
        /// <param name="slope">
        /// The slope value for the calibration.
        /// </param>
        /// <param name="intercept">
        /// The intercept for the calibration.
        /// </param>
        /// <param name="isAutoCalibrating">
        /// Optional argument that should be set to true if calibration is automatic. Defaults to false.
        /// When true, sets CalibrationDone to 1
        /// </param>
        /// <param name="manuallyCalibrating">
        /// Optional argument that should be set to true if manually defining the calibration slope and intercept. Defaults to false.
        /// When true, sets CalibrationDone to -1
        /// </param>
        /// <remarks>This function is called by the AutoCalibrateUIMF DLL</remarks>
        public void UpdateAllCalibrationCoefficients(
            double slope,
            double intercept,
            bool isAutoCalibrating = false,
            bool manuallyCalibrating = false)
        {
            var hasLegacyFrameParameters = TableExists(FRAME_PARAMETERS_TABLE);
            var hasFrameParamsTable = TableExists(FRAME_PARAMS_TABLE);

            using (var dbCommand = mDbConnection.CreateCommand())
            {
                if (hasLegacyFrameParameters)
                {
                    dbCommand.CommandText = "UPDATE " + FRAME_PARAMETERS_TABLE + " " +
                                            "SET CalibrationSlope = " + slope + ", " +
                                            "CalibrationIntercept = " + intercept;

                    if (isAutoCalibrating)
                    {
                        dbCommand.CommandText += ", CalibrationDone = 1";
                    }
                    else if (manuallyCalibrating)
                    {
                        dbCommand.CommandText += ", CalibrationDone = -1";
                    }

                    dbCommand.ExecuteNonQuery();
                }

                if (!hasFrameParamsTable)
                {
                    return;
                }

                // Update existing values
                dbCommand.CommandText = "UPDATE " + FRAME_PARAMS_TABLE + " " +
                                        "SET ParamValue = " + slope + " " +
                                        "WHERE ParamID = " + (int)FrameParamKeyType.CalibrationSlope;
                dbCommand.ExecuteNonQuery();

                dbCommand.CommandText = "UPDATE " + FRAME_PARAMS_TABLE + " " +
                                        "SET ParamValue = " + intercept + " " +
                                        "WHERE ParamID = " + (int)FrameParamKeyType.CalibrationIntercept;
                dbCommand.ExecuteNonQuery();

                // Add new values for any frames that do not have slope or intercept defined as frame params
                AssureAllFramesHaveFrameParam(dbCommand, FrameParamKeyType.CalibrationSlope,
                    slope.ToString(CultureInfo.InvariantCulture));

                AssureAllFramesHaveFrameParam(dbCommand, FrameParamKeyType.CalibrationIntercept,
                    intercept.ToString(CultureInfo.InvariantCulture));

                string newCalibrationDone;
                if (isAutoCalibrating)
                {
                    newCalibrationDone = "1";
                }
                else if (manuallyCalibrating)
                {
                    newCalibrationDone = "-1";
                }
                else
                {
                    return;
                }

                dbCommand.CommandText = " UPDATE " + FRAME_PARAMS_TABLE +
                                        " SET ParamValue = " + newCalibrationDone +
                                        " WHERE ParamID = " + (int)FrameParamKeyType.CalibrationDone;
                dbCommand.ExecuteNonQuery();

                // Add new values for any frames that do not have slope or intercept defined as frame params
                AssureAllFramesHaveFrameParam(dbCommand, FrameParamKeyType.CalibrationDone, newCalibrationDone);

            }
        }

        /// <summary>
        /// Update the slope and intercept for all frames
        /// </summary>
        /// <param name="dBConnection"></param>
        /// <param name="slope">
        /// The slope value for the calibration.
        /// </param>
        /// <param name="intercept">
        /// The intercept for the calibration.
        /// </param>
        /// <param name="isAutoCalibrating">
        /// Optional argument that should be set to true if calibration is automatic. Defaults to false.
        /// </param>
        /// <remarks>This function is called by the AutoCalibrateUIMF DLL</remarks>
        [Obsolete("Instantiate a DataWriter, and use the non-static version of UpdateAllCalibrationCoefficients.", true)]
        public static void UpdateAllCalibrationCoefficients(
            SQLiteConnection dBConnection,
            double slope,
            double intercept,
            bool isAutoCalibrating = false)
        {
            throw new Exception("Static UpdateAllCalibrationCoefficients is obsolete, use non-static UpdateAllCalibrationCoefficients");
        }

        /// <summary>
        /// Update the slope and intercept for the given frame
        /// </summary>
        /// <param name="frameNumber">
        /// The frame number to update.
        /// </param>
        /// <param name="slope">
        /// The slope value for the calibration.
        /// </param>
        /// <param name="intercept">
        /// The intercept for the calibration.
        /// </param>
        /// <param name="isAutoCalibrating">
        /// Optional argument that should be set to true if calibration is automatic. Defaults to false.
        /// </param>
        /// <remarks>This function is called by the AutoCalibrateUIMF DLL</remarks>
        public void UpdateCalibrationCoefficients(int frameNumber, double slope, double intercept, bool isAutoCalibrating = false)
        {
            AddUpdateFrameParameter(frameNumber, FrameParamKeyType.CalibrationSlope, slope.ToString(CultureInfo.InvariantCulture));
            AddUpdateFrameParameter(frameNumber, FrameParamKeyType.CalibrationIntercept, intercept.ToString(CultureInfo.InvariantCulture));
            if (isAutoCalibrating)
            {
                AddUpdateFrameParameter(frameNumber, FrameParamKeyType.CalibrationDone, "1");
            }
        }

        /// <summary>
        /// Update the slope and intercept for the given frame
        /// </summary>
        /// <param name="dBConnection"></param>
        /// <param name="frameNumber">
        /// The frame number to update.
        /// </param>
        /// <param name="slope">
        /// The slope value for the calibration.
        /// </param>
        /// <param name="intercept">
        /// The intercept for the calibration.
        /// </param>
        /// <param name="isAutoCalibrating">
        /// Optional argument that should be set to true if calibration is automatic. Defaults to false.
        /// </param>
        /// <remarks>This function is called by the AutoCalibrateUIMF DLL</remarks>
        [Obsolete("Instantiate a DataWriter, and use the non-static version of UpdateCalibrationCoefficients.")]
        public static void UpdateCalibrationCoefficients(
            SQLiteConnection dBConnection,
            int frameNumber,
            double slope,
            double intercept,
            bool isAutoCalibrating = false)
        {
            throw new Exception("Static UpdateCalibrationCoefficients is obsolete, use non-static UpdateCalibrationCoefficients");
        }

        /// <summary>
        /// Add or update a the value of a given parameter in a frame
        /// </summary>
        /// <param name="frameNumber">
        /// </param>
        /// <param name="parameterName">
        /// </param>
        /// <param name="parameterValue">
        /// </param>
        [Obsolete("Use AddUpdateFrameParameter")]
        public void UpdateFrameParameter(int frameNumber, string parameterName, string parameterValue)
        {
            // Resolve parameter name to param key
            var paramType = FrameParamUtilities.GetParamTypeByName(parameterName);

            if (paramType == FrameParamKeyType.Unknown)
                throw new ArgumentOutOfRangeException(nameof(parameterName), "Unrecognized parameter name " + parameterName + "; cannot update");

            AddUpdateFrameParameter(frameNumber, paramType, parameterValue);

        }

        /// <summary>
        /// </summary>
        /// <param name="frameNumber">
        /// </param>
        /// <param name="parameters">
        /// </param>
        /// <param name="values">
        /// </param>
        [Obsolete("Use AddUpdateFrameParameter")]
        public void UpdateFrameParameters(int frameNumber, List<string> parameters, List<string> values)
        {
            for (var i = 0; i < parameters.Count - 1; i++)
            {
                if (i >= values.Count)
                    break;

                UpdateFrameParameter(frameNumber, parameters[i], values[i]);
            }
        }

        /// <summary>
        /// Updates the scan count for the given frame
        /// </summary>
        /// <param name="frameNum">
        /// The frame number to update
        /// </param>
        /// <param name="NumScans">
        /// The new scan count
        /// </param>
        public void UpdateFrameScanCount(int frameNum, int NumScans)
        {
            AddUpdateFrameParameter(frameNum, FrameParamKeyType.Scans, NumScans.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// This function updates the frame type to 1, 2, 2, 2, 1, 2, 2, 2, etc. for the specified frame range
        /// It is used in the NUnit tests
        /// </summary>
        /// <param name="startFrameNum">
        /// The start Frame Num.
        /// </param>
        /// <param name="endFrameNum">
        /// The end Frame Num.
        /// </param>
        public void UpdateFrameType(int startFrameNum, int endFrameNum)
        {
            for (var i = startFrameNum; i <= endFrameNum; i++)
            {
                var frameType = i % 4 == 0 ? 1 : 2;
                AddUpdateFrameParameter(i, FrameParamKeyType.FrameType, frameType.ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// Assures that NumFrames in the Global_Params table matches the number of frames in the Frame_Params table
        /// </summary>
        [Obsolete("Use UpdateGlobalStats")]
        public void UpdateGlobalFrameCount()
        {
            UpdateGlobalStats();
        }

        /// <summary>
        /// Assures that NumFrames in the Global_Params table matches the number of frames in the Frame_Params table
        /// Also assures that PrescanTOFPulses lists the maximum scan number in any frame
        /// </summary>
        public void UpdateGlobalStats()
        {
            if (!HasFrameParamsTable)
                throw new Exception("UIMF file does not have table Frame_Params; use method CreateTables to add tables");

            using (var dbCommand = mDbConnection.CreateCommand())
            {

                dbCommand.CommandText = "SELECT Count (Distinct FrameNum) FROM " + FRAME_PARAMS_TABLE;
                var frameCountObj = dbCommand.ExecuteScalar();

                if (frameCountObj != null && frameCountObj != DBNull.Value)
                {
                    AddUpdateGlobalParameter(GlobalParamKeyType.NumFrames, Convert.ToInt32(frameCountObj));
                }
            }

            var frameCount = mGlobalParameters.NumFrames;

            object maxScanFromQuery;
            using (var dbCommand = mDbConnection.CreateCommand())
            {

                dbCommand.CommandText = "SELECT Max(ScanNum) FROM " + FRAME_SCANS_TABLE;
                maxScanFromQuery = dbCommand.ExecuteScalar();
            }

            if (maxScanFromQuery == null || maxScanFromQuery == DBNull.Value)
                return;

            var maxScan = Convert.ToInt32(maxScanFromQuery);
            if (maxScan < 1)
                return;

            // PrescanTOFPulses tracks the maximum scan number in any frame
            var existingValue = GetGlobalParams().GetValue(GlobalParamKeyType.PrescanTOFPulses, 0);
            bool updateGlobalParams;

            if (existingValue > 0 && existingValue > maxScan)
            {
                // Update the value only if the new value is more than 5% less than the existing value
                var percentDiff = (existingValue - frameCount) / (float)existingValue;
                updateGlobalParams = percentDiff > 0.05;
            }
            else
            {
                updateGlobalParams = true;
            }

            if (updateGlobalParams)
            {
                // Round up maxScan to the nearest 10, 100, or 1000
                int divisor;
                if (maxScan <= 100)
                {
                    // When maxScan is between 1 and 100, round to the nearest 10
                    divisor = 10;
                }
                else
                {
                    // When between 100 and 1000, round up to the nearest 10
                    // When between 1000 and 10000, round up to the nearest 100
                    var powerExponent = (int)Math.Ceiling(Math.Log10(maxScan));
                    divisor = (int)Math.Pow(10, powerExponent - 2);
                }

                while (maxScan % divisor != 0)
                {
                    maxScan++;
                }

                AddUpdateGlobalParameter(GlobalParamKeyType.PrescanTOFPulses, maxScan);
            }
        }

        /// <summary>
        /// </summary>
        /// <param name="tableName">
        /// </param>
        /// <param name="fileBytesAsBuffer">
        /// </param>
        /// <returns>
        /// Always returns true<see cref="bool"/>.
        /// </returns>
        public bool WriteFileToTable(string tableName, byte[] fileBytesAsBuffer)
        {
            using (var dbCommand = mDbConnection.CreateCommand())
            {
                if (!TableExists(tableName))
                {
                    // Create the table
                    dbCommand.CommandText = "CREATE TABLE " + tableName + " (FileText BLOB);";
                    dbCommand.ExecuteNonQuery();
                }
                else
                {
                    // Delete the data currently in the table
                    dbCommand.CommandText = "DELETE FROM " + tableName + ";";
                    dbCommand.ExecuteNonQuery();
                }

                dbCommand.CommandText = "INSERT INTO " + tableName + " VALUES (:Buffer);";

                dbCommand.Parameters.Add(new SQLiteParameter(":Buffer", fileBytesAsBuffer));

                dbCommand.ExecuteNonQuery();
            }

            return true;
        }

        #endregion

        #region Methods

        /// <summary>
        /// Add a column to the legacy Frame_Parameters table
        /// </summary>
        /// <param name="parameterName">
        /// </param>
        /// <param name="parameterType">
        /// </param>
        /// <remarks>
        /// The new column will have Null values for all existing rows
        /// </remarks>
        private void AddFrameParameter(string parameterName, string parameterType)
        {
            try
            {
                var dbCommand = mDbConnection.CreateCommand();
                dbCommand.CommandText = "Alter TABLE " + FRAME_PARAMETERS_TABLE + " Add " + parameterName + " " + parameterType;
                dbCommand.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error adding parameter " + parameterName + " to the legacy Frame_Parameters table:" + ex.Message);
            }
        }

        /// <summary>
        /// Add a column to the legacy Frame_Parameters table
        /// </summary>
        /// <param name="parameterName">
        /// Parameter name (aka column name in the database)
        /// </param>
        /// <param name="parameterType">
        /// Parameter type
        /// </param>
        /// <param name="defaultValue">
        /// Value to assign to all rows
        /// </param>
        private void AddFrameParameter(string parameterName, string parameterType, int defaultValue)
        {
            AddFrameParameter(parameterName, parameterType);

            try
            {
                var dbCommand = mDbConnection.CreateCommand();
                dbCommand.CommandText = "UPDATE " + FRAME_PARAMETERS_TABLE + " " +
                                        "SET " + parameterName + " = " + defaultValue + " " +
                                        "WHERE " + parameterName + " IS NULL";
                dbCommand.ExecuteNonQuery();

            }
            catch (Exception ex)
            {
                Console.WriteLine("Error setting default value for legacy frame parameter " + parameterName + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Creates the table creation DDL using the table name and field info
        /// </summary>
        /// <param name="tableName">
        /// Table name
        /// </param>
        /// <param name="lstFields">
        /// List of Tuples where Item1 is FieldName, Item2 is Sql data type, and Item3 is .NET data type
        /// </param>
        /// <returns></returns>
        private string GetCreateTableSql(string tableName, IList<Tuple<string, string, string>> lstFields)
        {
            // Construct a Sql Statement of the form
            // CREATE TABLE Frame_Scans (FrameNum INTEGER NOT NULL, ParamID INTEGER NOT NULL, Value TEXT)";

            var sbSql = new StringBuilder("CREATE TABLE " + tableName + " ( ");

            for (var i = 0; i < lstFields.Count; i++)
            {
                sbSql.Append(lstFields[i].Item1 + " " + lstFields[i].Item2);

                if (i < lstFields.Count - 1)
                {
                    sbSql.Append(", ");
                }
            }

            sbSql.Append(");");

            return sbSql.ToString();
        }

        /// <summary>
        /// Add entries to the legacy Frame_Parameters table
        /// </summary>
        /// <param name="frameNum"></param>
        /// <param name="frameParameters"></param>
        private void InsertLegacyFrameParams(int frameNum, FrameParams frameParameters)
        {

            var legacyFrameParameters = FrameParamUtilities.GetLegacyFrameParameters(frameNum, frameParameters);

            InitializeFrameParametersRow(legacyFrameParameters);

        }

        /// <summary>
        /// Add entries to the legacy Frame_Parameters table
        /// </summary>
        /// <param name="frameNum"></param>
        /// <param name="frameParamsByType"></param>
        private void InsertLegacyFrameParams(int frameNum, Dictionary<FrameParamKeyType, dynamic> frameParamsByType)
        {
            var frameParams = FrameParamUtilities.ConvertDynamicParamsToFrameParams(frameParamsByType);

            InsertLegacyFrameParams(frameNum, frameParams);
        }

        /// <summary>
        /// Add a parameter to the legacy Global_Parameters table
        /// </summary>
        /// <param name="dbCommand"></param>
        /// <param name="paramKey"></param>
        /// <param name="paramValue"></param>
        private void InsertLegacyGlobalParameter(IDbCommand dbCommand, GlobalParamKeyType paramKey, string paramValue)
        {
            if (!mLegacyGlobalParametersTableHasData)
            {
                // Check for an existing row in the legacy Global_Parameters table
                dbCommand.CommandText = "SELECT COUNT(*) FROM " + GLOBAL_PARAMETERS_TABLE;
                var rowCount = (long)(dbCommand.ExecuteScalar());

                if (rowCount < 1)
                {
#pragma warning disable 612, 618
                    var legacyGlobalParameters = new GlobalParameters
                    {
                        DateStarted = string.Empty,
                        NumFrames = mGlobalParameters.NumFrames,
                        InstrumentName = string.Empty,
                        Prescan_Profile = string.Empty,
                        TOFIntensityType = string.Empty
                    };
#pragma warning restore 612, 618

                    InitializeGlobalParametersRow(legacyGlobalParameters);
                }
                mLegacyGlobalParametersTableHasData = true;
            }

            var fieldMapping = GetLegacyGlobalParameterMapping();
            var legacyFieldName = (from item in fieldMapping where item.Value == paramKey select item.Key).ToList();
            if (legacyFieldName.Count > 0)
            {
                dbCommand.CommandText = "UPDATE " + GLOBAL_PARAMETERS_TABLE + " " +
                                        "SET " + legacyFieldName.First() + " = '" + paramValue + "' ";
                dbCommand.ExecuteNonQuery();
            }
            else
            {
                Console.WriteLine("Skipping unsupported key type, " + paramKey);
            }
        }

        /// <summary>
        /// </summary>
        /// <param name="frameNumber">
        /// </param>
        /// <param name="scanNum">
        /// </param>
        /// <param name="nonZeroCount">
        /// </param>
        /// <param name="bpi">
        /// </param>
        /// <param name="bpiMz">
        /// </param>
        /// <param name="tic">
        /// </param>
        /// <param name="spectraRecord">
        /// </param>
        private void InsertScanAddParameters(
            int frameNumber,
            int scanNum,
            int nonZeroCount,
            int bpi,
            double bpiMz,
            long tic,
            IEnumerable spectraRecord)
        {
            mDbCommandInsertScan.Parameters.Clear();
            mDbCommandInsertScan.Parameters.Add(new SQLiteParameter("FrameNum", frameNumber));
            mDbCommandInsertScan.Parameters.Add(new SQLiteParameter("ScanNum", scanNum));
            mDbCommandInsertScan.Parameters.Add(new SQLiteParameter("NonZeroCount", nonZeroCount));
            mDbCommandInsertScan.Parameters.Add(new SQLiteParameter("BPI", bpi));
            mDbCommandInsertScan.Parameters.Add(new SQLiteParameter("BPI_MZ", bpiMz));
            mDbCommandInsertScan.Parameters.Add(new SQLiteParameter("TIC", tic));
            mDbCommandInsertScan.Parameters.Add(new SQLiteParameter("Intensities", spectraRecord));
        }

        /// <summary>
        /// Create command for inserting frames
        /// </summary>
        private void PrepareInsertFrameParamKey()
        {
            mDbCommandInsertFrameParamKey = mDbConnection.CreateCommand();

            mDbCommandInsertFrameParamKey.CommandText = "INSERT INTO " + FRAME_PARAM_KEYS_TABLE + " (ParamID, ParamName, ParamDataType, ParamDescription) " +
                                                         "VALUES (:ParamID, :ParamName, :ParamDataType, :ParamDescription);";
        }

        /// <summary>
        /// Create command for inserting frame parameters
        /// </summary>
        private void PrepareInsertFrameParamValue()
        {
            mDbCommandInsertFrameParamValue = mDbConnection.CreateCommand();

            mDbCommandInsertFrameParamValue.CommandText = "INSERT INTO " + FRAME_PARAMS_TABLE + " (FrameNum, ParamID, ParamValue) " +
                                                           "VALUES (:FrameNum, :ParamID, :ParamValue);";
        }

        /// <summary>
        /// Create command for updating frame parameters
        /// </summary>
        private void PrepareUpdateFrameParamValue()
        {
            mDbCommandUpdateFrameParamValue = mDbConnection.CreateCommand();

            mDbCommandUpdateFrameParamValue.CommandText = "UPDATE " + FRAME_PARAMS_TABLE + " " +
                                                          "SET ParamValue = :ParamValue" +
                                                          "WHERE FrameNum = :FrameNum AND ParamID = :ParamID";
        }

        /// <summary>
        /// Create command for inserting legacy frame parameters
        /// </summary>
        private void PrepareInsertLegacyFrameParamValue()
        {
            mDbCommandInsertLegacyFrameParameterRow = mDbConnection.CreateCommand();

            mDbCommandInsertLegacyFrameParameterRow.CommandText =
                "INSERT INTO " + FRAME_PARAMETERS_TABLE + " ("
                  + "FrameNum, StartTime, Duration, Accumulations, FrameType, Scans, IMFProfile, TOFLosses,"
                  + "AverageTOFLength, CalibrationSlope, CalibrationIntercept,a2, b2, c2, d2, e2, f2, Temperature, voltHVRack1, voltHVRack2, voltHVRack3, voltHVRack4, "
                  + "voltCapInlet, voltEntranceHPFIn, voltEntranceHPFOut, "
                  + "voltEntranceCondLmt, voltTrapOut, voltTrapIn, voltJetDist, voltQuad1, voltCond1, voltQuad2, voltCond2, "
                  + "voltIMSOut, voltExitHPFIn, voltExitHPFOut, "
                  + "voltExitCondLmt, PressureFront, PressureBack, MPBitOrder, FragmentationProfile, HighPressureFunnelPressure, IonFunnelTrapPressure, "
                  + "RearIonFunnelPressure, QuadrupolePressure, ESIVoltage, FloatVoltage, CalibrationDone, Decoded)"
                + "VALUES (:FrameNum, :StartTime, :Duration, :Accumulations, :FrameType,:Scans,:IMFProfile,:TOFLosses,"
                  + ":AverageTOFLength,:CalibrationSlope,:CalibrationIntercept,:a2,:b2,:c2,:d2,:e2,:f2,:Temperature,:voltHVRack1,:voltHVRack2,:voltHVRack3,:voltHVRack4, "
                  + ":voltCapInlet,:voltEntranceHPFIn,:voltEntranceHPFOut,"
                  + ":voltEntranceCondLmt,:voltTrapOut,:voltTrapIn,:voltJetDist,:voltQuad1,:voltCond1,:voltQuad2,:voltCond2,"
                  + ":voltIMSOut,:voltExitHPFIn,:voltExitHPFOut,:voltExitCondLmt, "
                  + ":PressureFront,:PressureBack,:MPBitOrder,:FragmentationProfile, " + ":HPPressure, :IPTrapPressure, "
                  + ":RIFunnelPressure, :QuadPressure, :ESIVoltage, :FloatVoltage, :CalibrationDone, :Decoded);";

        }

        /// <summary>
        /// Create command for inserting global parameters
        /// </summary>
        private void PrepareInsertGlobalParamValue()
        {
            mDbCommandInsertGlobalParamValue = mDbConnection.CreateCommand();

            mDbCommandInsertGlobalParamValue.CommandText =
                "INSERT INTO " + GLOBAL_PARAMS_TABLE + " " +
                "(ParamID, ParamName, ParamValue, ParamDataType, ParamDescription) " +
                "VALUES (:ParamID, :ParamName, :ParamValue, :ParamDataType, :ParamDescription);";
        }

        /// <summary>
        /// Create command for updating global parameters
        /// </summary>
        private void PrepareUpdateGlobalParamValue()
        {
            mDbCommandUpdateGlobalParamValue = mDbConnection.CreateCommand();

            mDbCommandUpdateGlobalParamValue.CommandText = "UPDATE " + GLOBAL_PARAMS_TABLE + " " +
                                                           "SET ParamValue = :ParamValue " +
                                                           "WHERE ParamID = :ParamID";
        }

        /// <summary>
        /// Create command for inserting scans
        /// </summary>
        private void PrepareInsertScan()
        {
            // This function should be called before looping through each frame and scan
            mDbCommandInsertScan = mDbConnection.CreateCommand();
            mDbCommandInsertScan.CommandText =
                "INSERT INTO " + FRAME_SCANS_TABLE + " (FrameNum, ScanNum, NonZeroCount, BPI, BPI_MZ, TIC, Intensities) "
                + "VALUES(:FrameNum, :ScanNum, :NonZeroCount, :BPI, :BPI_MZ, :TIC, :Intensities);";

        }

        /// <summary>
        /// Begin a transaction
        /// </summary>
        private void TransactionBegin()
        {
            using (var dbCommand = mDbConnection.CreateCommand())
            {
                dbCommand.CommandText = "PRAGMA synchronous=0;BEGIN TRANSACTION;";
                dbCommand.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Commit a transaction
        /// </summary>
        private void TransactionCommit()
        {
            using (var dbCommand = mDbConnection.CreateCommand())
            {
                dbCommand.CommandText = "END TRANSACTION;PRAGMA synchronous=1;";
                dbCommand.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Update a parameter in the legacy Frame_Parameters table
        /// </summary>
        /// <param name="frameNum">Frame number to update</param>
        /// <param name="paramKeyType">Key type</param>
        /// <param name="paramValue">Value</param>
        /// <param name="dbCommand">database command object</param>
        private void UpdateLegacyFrameParameter(int frameNum, FrameParamKeyType paramKeyType, string paramValue, IDbCommand dbCommand)
        {
            // Make sure the Frame_Parameters table has the Decoded column
            ValidateLegacyDecodedColumnExists();

            var fieldMapping = GetLegacyFrameParameterMapping();
            var legacyFieldName = (from item in fieldMapping where item.Value == paramKeyType select item.Key).ToList();
            if (legacyFieldName.Count > 0)
            {
                dbCommand.CommandText = "UPDATE " + FRAME_PARAMETERS_TABLE + " " +
                                        "SET " + legacyFieldName.First() + " = '" + paramValue + "' " +
                                        "WHERE frameNum = " + frameNum;
                dbCommand.ExecuteNonQuery();
            }
            else
            {
                Console.WriteLine("Skipping unsupported key type, " + paramKeyType);
            }

        }

        /// <summary>
        /// Assures that the Frame_Params_Keys table contains an entry for paramKeyType
        /// </summary>
        protected void ValidateFrameParameterKey(FrameParamKeyType paramKeyType)
        {
            var keyTypeList = new List<FrameParamKeyType>
            {
                paramKeyType
            };

            ValidateFrameParameterKeys(keyTypeList);
        }

        /// <summary>
        /// Assures that the Frame_Params_Keys table contains each of the keys in paramKeys
        /// </summary>
        protected void ValidateFrameParameterKeys(List<FrameParamKeyType> paramKeys)
        {
            var updateRequired = false;

            foreach (var newKey in paramKeys)
            {
                if (!mFrameParameterKeys.ContainsKey(newKey))
                {
                    updateRequired = true;
                    break;
                }
            }

            if (!updateRequired)
                return;

            // Assure that mFrameParameterKeys is synchronized with the .UIMF file
            // Obtain the current contents of Frame_Param_Keys
            mFrameParameterKeys = GetFrameParameterKeys(mDbConnection);

            // Add any new keys not yet in Frame_Param_Keys
            foreach (var newKey in paramKeys)
            {
                if (!mFrameParameterKeys.ContainsKey(newKey))
                {
                    var paramDef = FrameParamUtilities.GetParamDefByType(newKey);

                    try
                    {
                        mDbCommandInsertFrameParamKey.Parameters.Clear();
                        mDbCommandInsertFrameParamKey.Parameters.Add(new SQLiteParameter("ParamID", paramDef.ParamType));
                        mDbCommandInsertFrameParamKey.Parameters.Add(new SQLiteParameter("ParamName", paramDef.Name));
                        mDbCommandInsertFrameParamKey.Parameters.Add(new SQLiteParameter("ParamDataType", paramDef.DataType.FullName));
                        mDbCommandInsertFrameParamKey.Parameters.Add(new SQLiteParameter("ParamDescription", paramDef.Description));

                        mDbCommandInsertFrameParamKey.ExecuteNonQuery();
                    }
                    catch (Exception ex)
                    {
                        ReportError("Exception adding parameter " + paramDef.Name + " to table " + FRAME_PARAM_KEYS_TABLE + ": " + ex.Message, ex);
                        throw;
                    }

                    mFrameParameterKeys.Add(paramDef.ParamType, paramDef);
                }
            }

        }

        /// <summary>
        /// Assures column Decoded exists in the legacy Frame_Parameters table
        /// </summary>
        protected void ValidateLegacyDecodedColumnExists()
        {
            if (mLegacyFrameParameterTableHasDecodedColumn)
                return;

            if (!HasLegacyParameterTables)
                return;

            if (!TableHasColumn(FRAME_PARAMETERS_TABLE, "Decoded"))
            {
                AddFrameParameter("Decoded", "INT", 0);
            }

            mLegacyFrameParameterTableHasDecodedColumn = true;
        }

        /// <summary>
        /// Assures that several columns exist in the legacy Frame_Parameters table
        /// </summary>
        /// <remarks>
        /// This method is used when writing data to legacy tables
        /// in a UIMF file that was cloned from an old file format
        /// </remarks>
        public void ValidateLegacyHPFColumnsExist()
        {
            if (mLegacyFrameParameterTableHaHPFColumns)
                return;

            if (!HasLegacyParameterTables)
                return;

            if (!TableHasColumn(FRAME_PARAMETERS_TABLE, "voltEntranceHPFIn"))
            {
                AddFrameParameter("voltEntranceHPFIn", "DOUBLE", 0);
                AddFrameParameter("VoltEntranceHPFOut", "DOUBLE", 0);
            }

            if (!TableHasColumn(FRAME_PARAMETERS_TABLE, "voltExitHPFIn"))
            {
                AddFrameParameter("voltExitHPFIn", "DOUBLE", 0);
                AddFrameParameter("voltExitHPFOut", "DOUBLE", 0);
            }

            mLegacyFrameParameterTableHaHPFColumns = true;
        }

        #endregion

        #region Event Handlers

        private void FrameShifter_FrameShiftEvent(object sender, FrameNumShiftEventArgs e)
        {

            PostLogEntry(
                "Normal",
                string.Format("Decremented frame number by {0} for frames {1}", e.DecrementAmount, e.FrameRanges),
                "ShiftFramesInBatch");
        }

        #endregion
    }
}