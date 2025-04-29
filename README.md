# LMU_WeatherPlugin
SimHub Plugin to fetch weather forecast from REST API

This project has been made entirely with Chat-GPT, as i know close to nothing about C#, so feel free to think bad about the code. It's not my code anyway :)

This plugin makes a GET request to "http://localhost:6397/rest/sessions/weather/{_currentSession}", parses the json and creates all the corresponding properties inside SimHub
I also added the following:

WeatherPlugin.CurrentSessionLengthMinutes <- this comes from "http://localhost:6397/rest/sessions/GetSessionsInfoForEvent" and takes the current session duration, needed for the following:
WeatherPlugin.CurrentNodeDurationMinutes <- since the Node Duration value seems fixed to 30 minutes (but it's not how the game really works), we are calculating this simply by CurrentSessionLenghtMinutes/5

There's also anoter Computed sets of properties, CURRENTNODE_property, that returns the current node by taking into account session duraation and session elapsed time
