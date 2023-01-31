using System;
//using System.Net.Http;
//using System.Text.Json;
using Newtonsoft.Json;
//using Newtonsoft.Json.Linq;

//Mock CPH

public class CPHmock
{
    private string currentScene = "RocksmithBigCam";
    public void LogDebug(string str) { Console.WriteLine(str); }
    public void LogInfo(string str) { Console.WriteLine(str); }
    public void LogError(string str) { Console.WriteLine(str); }

    public void LogVerbose(string str) { Console.WriteLine(str); }

    public void ObsSetScene(string str) { Console.WriteLine(string.Format("Setting scene: {0}", str)); currentScene = str; }

    public string ObsGetCurrentScene() { return currentScene; }
	
    public void ObsSetGdiText(string scene, string source, string text, int connection =0){ Console.WriteLine(string.Format("Setting text field {1} in scene {0} to {2}", scene, source, text)); }
    
    public void SendMessage(string str) { Console.WriteLine(str); }

    public void RunAction(string str) { Console.WriteLine(string.Format("Running action: {0}",str)); }

    public string GetGlobalVar<Type>(string key)
    {
        string value = "";
        if (key.Equals("snifferIP")) value = "192.168.1.37";
        if (key.Equals("snifferPort")) value = "9938";
        if (key.Equals("songScene")) value = "RocksmithBigCamInGame";
        if (key.Equals("rocksmithScene")) value = "RocksmithBigCam";
        if (key.Equals("pauseScene")) value = "RocksmithBigCam";
        if (key.Equals("sectionDetection")) value = "True";

        return value;

    }
    public void SetGlobalVar(string varName, object value, bool persisted = true)
    {
        Console.WriteLine(string.Format("Writing value {1} to variable {0}",varName,value));
    }
    public static void Main(string[] args )
    { 
        CPHInline obj = new CPHInline();

        obj.Init();
        int i = 0;
        while (true)
        {   
            if (((++i) % 10) == 0) Console.Clear();
            obj.Execute();
            Thread.Sleep(1000);
        }
    }

}


// Objects for parsing the song data
// 

record MemoryReadout
{
    [JsonRequired]
    public string SongId { get; set; } = null!;
    [JsonRequired]
    public string ArrangementId { get; set; } = null!;
    [JsonRequired]
    public string GameStage { get; set; } = null!;
    public double SongTimer { get; set; }
    public NoteData NoteData { get; set; } = null!;
}
record NoteData
{
    public double Accuracy { get; set; }
    public int TotalNotes { get; set; }
    public int TotalNotesHit { get; set; }
    public int CurrentHitStreak { get; set; }

    public int HighestHitStreak { get; set; }
    public int TotalNotesMissed { get; set; }
    public int CurrentMissStreak { get; set; }
}
record SongDetails
{
    public string SongName { get; set; } = null!;
    public string ArtistName { get; set; } = null!;
    public double SongLength { get; set; }
    public string AlbumName { get; set; } = null!;
    public int AlbumYear { get; set; }
    public Arrangement[] Arrangements { get; set; } = null!;

}


record Arrangement
{
    public string Name { get; set; } = null!;
    public string ArrangementID { get; set; } = null!;
    public string type { get; set; } = null!;
    public Tuning Tuning { get; set; } = null!;

    public Section[] Sections { get; set; } = null!;
}
record Tuning
{
    public string TuningName { get; set; } = null!;
}

record Section
{
    public string Name { get; set; } = null!;
    public double StartTime { get; set; }
    public double EndTime { get; set; }
}
record Response
{
    //It does not give any performance boost to parse only partially, due to the way the parser works.
    //However parsing the full song takes roughly 0.2 micro seconds, so it's pretty neglectable
    public MemoryReadout MemoryReadout { get; set; } = null!;
    public SongDetails SongDetails { get; set; } = null!;
}

//Implementation for Streamer.bot

public class CPHInline
{
    enum GameStage
    {
        Menu
        , InSong
        , InTuner
    }

    enum SectionType
    {
        Default
        ,Riff
        ,Solo
        ,Verse
        ,Chorus
        ,Brigde
        ,Breakdown
    }

    private string snifferIp = null!;
    private string snifferPort = null!;


    private GameStage currentGameStage;
    private GameStage lastGameStage;
    private SectionType currentSectionType;
    private SectionType lastSectionType;
    private double currentSongTimer;
    private double lastSongTimer;

    private Arrangement? currentArrangement = null!;
    private int currentSectionIndex;
   
    //Split into memory details and SongDetails, as it is only necessary to parse the latter once
    private Response currentResponse = null!;
    private NoteData lastNoteData = null!;

    private string rocksmithScene = null!;
    private string songScene = null!;
    private string songPausedScene = null!;
	private string currentScene = null!;
	
    private HttpClient client = null!;
    private HttpResponseMessage response = null!;
    private string responseString = null!;

    private DateTime lastSceneChange;
    private int minDelay;

    //Needs to be commented out in streamer bot.
    private CPHmock CPH = new CPHmock();

    private bool doLogToChat = false;
    // Disabling regular verbose request as they really bloat the log file rather quickly. Can be enabled if need be
    private bool doLogVerbose = false;

    private bool isSectionDetectionActive = false;

    void debug(string str)
    {
        if (doLogToChat) CPH.SendMessage(str);
        CPH.LogDebug(str);
    }

    private GameStage evalGameStage(string stage)
    {
        GameStage currentStage = GameStage.Menu;
        // Other potential values are: MainMenu las_SongList las_SongOptions las_tuner
        if (stage.Equals("las_game"))
        {
            currentStage = GameStage.InSong;
        }
        else if (stage.Equals("las_tuner"))
        {
            currentStage = GameStage.InTuner;
        }
        else
        {
            //Evaluated as Menu
        }

        return currentStage;
    }

    public void Init()
    {

        //Init happens before arguments are passed, therefore temporary globals are used.
        snifferIp = CPH.GetGlobalVar<string>("snifferIP").Replace('"',' ').Trim();//"192.168.1.37";
        snifferPort = "9938";
		rocksmithScene = CPH.GetGlobalVar<string>("rocksmithScene");
		songScene = CPH.GetGlobalVar<string>("songScene");
		songPausedScene = CPH.GetGlobalVar<string>("pauseScene");
		
        if (CPH.GetGlobalVar<string>("sectionDetection").ToLower().Contains("true") == true)
        {
            isSectionDetectionActive = true;
        }
        lastSceneChange = DateTime.Now;
        minDelay = 3;
        client = new HttpClient();
        if (client == null) debug("Failed instantiating HttpClient");
		currentScene = "";
        
        currentSectionIndex = -1;
        lastSectionType = currentSectionType = SectionType.Default;
    }

    private bool getLatestResponse()
    {
        bool success;
        try
        {
            string address = string.Format("http://{0}:{1}", snifferIp, snifferPort);
            response = client.GetAsync(address).GetAwaiter().GetResult();
            if (response != null)
            {
                response.EnsureSuccessStatusCode();
                responseString = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                success = true;
            }
            else
            {
                success = false;
                debug("Response is null");
            }
        }
        catch (HttpRequestException e)
        {
            debug("Error in response");
            debug(string.Format("Caught exception trying to get response from sniffer: {0}", e.Message));
            success = false;
        }
        if (!success) debug("Failed fetching response");
        return success;
    }

    private bool isRelevantScene()
    {
        bool isRelevant = false;
		currentScene = CPH.ObsGetCurrentScene();
		if (currentScene != null)
		if (currentScene.Equals(rocksmithScene)
		|| currentScene.Equals(songScene)
		|| currentScene.Equals(songPausedScene))
		{
			isRelevant = true;
		}
        return isRelevant;
    }

    private void parseLatestResponse()
    {
        try
        {             
            currentResponse = JsonConvert.DeserializeObject<Response>(responseString) ?? throw new Exception("Is never supposed to be zero");
            currentGameStage = evalGameStage(currentResponse.MemoryReadout.GameStage);
            currentSongTimer = currentResponse.MemoryReadout.SongTimer;
        }
        catch (JsonException ex)
        {
            debug("Error parsing response: " + ex.Message);
        }
        
    }

    private void saveSongMetaData()
    {
        CPH.SetGlobalVar("SongName", currentResponse.SongDetails.SongName, false);
        CPH.SetGlobalVar("ArtistName", currentResponse.SongDetails.ArtistName, false);
        CPH.SetGlobalVar("AlbumName", currentResponse.SongDetails.AlbumName, false);
        if (currentArrangement != null)
        {
            CPH.SetGlobalVar("Tuning", currentArrangement.Tuning, false);
        }
    }
    private void saveNoteDataIfNecessary()
    {
        if (currentGameStage == GameStage.InSong)
        {
            if (lastNoteData != currentResponse.MemoryReadout.NoteData)
            {
                CPH.SetGlobalVar("Accuracy", currentResponse.MemoryReadout.NoteData.Accuracy, false);
                CPH.SetGlobalVar("CurrentHitStreak", currentResponse.MemoryReadout.NoteData.CurrentHitStreak, false);
                CPH.SetGlobalVar("CurrentMissStreak", currentResponse.MemoryReadout.NoteData.CurrentMissStreak, false);
                CPH.SetGlobalVar("TotalNotes", currentResponse.MemoryReadout.NoteData.TotalNotes, false);
                CPH.SetGlobalVar("TotalNotesHit", currentResponse.MemoryReadout.NoteData.TotalNotesHit, false);
                CPH.SetGlobalVar("TotalNotesMissed", currentResponse.MemoryReadout.NoteData.TotalNotesMissed, false);
                lastNoteData = currentResponse.MemoryReadout.NoteData;
            }
        }
    }
    private void identifyArrangement()
    {
        currentArrangement = null;
        currentSectionIndex = -1;
        if (currentResponse.SongDetails != null) 
        { 
            foreach (Arrangement arr in currentResponse.SongDetails.Arrangements)
            {
                if (arr.ArrangementID == currentResponse.MemoryReadout.ArrangementId)
                {
					currentArrangement = arr;
                    break;
                }
            }
        }
        if (currentArrangement != null)
        {
            CPH.RunAction("ArrangementAvailable");
        }
        else
        {
            CPH.RunAction("NoArrangementAvailable");
        }
    }
    private void identifySection()
    {
        if (currentArrangement != null)
        {
            string name = currentArrangement.Sections[currentSectionIndex].Name;
            if (name.ToLower().Contains("solo")) { currentSectionType = SectionType.Solo; }
            else if (name.ToLower().Contains("riff")) { currentSectionType = SectionType.Riff; }
            else if (name.ToLower().Contains("bridge")) { currentSectionType = SectionType.Brigde; }
            else if (name.ToLower().Contains("breakdown")) { currentSectionType = SectionType.Breakdown; }
            else if (name.ToLower().Contains("chorus")) { currentSectionType = SectionType.Chorus; }
            else if (name.ToLower().Contains("verse")) { currentSectionType = SectionType.Verse; }
            else { currentSectionType = SectionType.Default; }
        }
        else
        { 
            currentSectionType = SectionType.Default; 
        }
    }

    private void performSceneSwitchIfNecessary()
    {
        if (currentGameStage == GameStage.InSong)
        {
            if (lastGameStage == GameStage.InTuner)
            {
                identifyArrangement();
                saveSongMetaData();
            }

            if (currentScene.Equals(rocksmithScene))
            {
                if (!currentResponse.MemoryReadout.SongTimer.Equals(lastSongTimer))
                {
                    if ((DateTime.Now - lastSceneChange).TotalSeconds > minDelay)
                    {
                        CPH.ObsSetScene(songScene);
                        lastSceneChange = DateTime.Now;
                    }
                }
                else
                {
                    //Already in correct scene
                }
            }
            else if (currentScene.Equals(songScene))
            {
                if (currentResponse.MemoryReadout.SongTimer.Equals(lastSongTimer))
                {
                    if ((DateTime.Now - lastSceneChange).TotalSeconds > minDelay)
                    {
                        CPH.ObsSetScene(songPausedScene);
                        lastSceneChange = DateTime.Now;
                    }
                }
            }
        }
        else if (currentGameStage == GameStage.Menu)
        {
            if (!currentScene.Equals(rocksmithScene))
            {
                if ((DateTime.Now - lastSceneChange).TotalSeconds > minDelay)
                {
                    CPH.ObsSetScene(rocksmithScene);
                    lastSceneChange = DateTime.Now;
                }
            }
        }
        lastGameStage = currentGameStage;
        lastSongTimer = currentResponse.MemoryReadout.SongTimer;
    }

    private void checkSectionActions()
    {
        if (currentArrangement != null)
        {
            bool hasSectionChanged = false;
            if (currentSectionIndex == -1)
            {
                if (currentSongTimer >= currentArrangement.Sections[0].StartTime)
                {
                    currentSectionIndex = 0;
                    hasSectionChanged = true;
                }
            }
            else
            {
                // Check if entered a new section
                if (currentSongTimer >= currentArrangement.Sections[currentSectionIndex].EndTime)
                {
                    ++currentSectionIndex;
                    hasSectionChanged = true;
                }
            }
            if (hasSectionChanged)
            {
				identifySection();
				//TODO: Should only happen if I execute it
				CPH.ObsSetGdiText("Projection(RS)","textSectionName",currentArrangement.Sections[currentSectionIndex].Name);
				if (currentSectionType != lastSectionType)
                {
                    CPH.RunAction(string.Format("leave{0}", Enum.GetName(typeof(SectionType),lastSectionType)));
                    CPH.RunAction(string.Format("enter{0}", Enum.GetName(typeof(SectionType),currentSectionType)));
                    lastSectionType = currentSectionType;
                }
            }
        }
    }

    public bool Execute()
    {
        if (isRelevantScene())
        {
            if (getLatestResponse())
            {
                parseLatestResponse();
                saveNoteDataIfNecessary();
                performSceneSwitchIfNecessary();
                
                if (isSectionDetectionActive)
                {
                    checkSectionActions();
                }
            }
            else
            {
                debug("Fetching response failed, exiting action.");
                return false;
            }
        }

        return true;
    }
}
