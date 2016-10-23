using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using ImageProcessorCore;
using Newtonsoft.Json;
using Tweetinvi;
using Tweetinvi.Parameters;
using Tweetinvi.Models;

namespace TheHexBot
{
    public class Program
    {
        private static string _previouslyProcessedTweetsFile = "previously.json";
        private static string _configurationFile = "configuration.json";
        private static string _generatedImagesFolder = "gen"; // don't end it with a slash

        private static List<long> _previouslyProcessedTweets = new List<long>();

        private static Configuration _configuration;

        public static void Main(string[] cmdlineArgs)
        {
            if (!File.Exists(_configurationFile))
            {
                Console.WriteLine("No configuration found. Can't continue");
                return;
            }

            if (!Directory.Exists(_generatedImagesFolder))
                Directory.CreateDirectory(_generatedImagesFolder);

            _configuration = JsonConvert.DeserializeObject<Configuration>(File.ReadAllText(_configurationFile));

            if (File.Exists(_previouslyProcessedTweetsFile))
                _previouslyProcessedTweets = JsonConvert.DeserializeObject<List<long>>(File.ReadAllText(_previouslyProcessedTweetsFile));
            
            Auth.SetUserCredentials(
                _configuration.ConsumerKey,
                _configuration.ConsumerSecret,
                _configuration.UserAccessToken,
                _configuration.UserAccessSecret);

            RateLimit.RateLimitTrackerMode = RateLimitTrackerMode.TrackOnly;

            TweetinviEvents.QueryBeforeExecute += (sender, args) => {

                var q = RateLimit.GetQueryRateLimit(args.QueryURL);

                System.Console.WriteLine("{0} {1} {2} {3}", args.QueryURL, q?.Limit, q?.Remaining, q?.ResetDateTimeInSeconds);

                if (q != null && q.Remaining == 0)
                        Console.WriteLine("Rate limit hit");
            };

            var mentionsTL = Timeline.GetMentionsTimeline();

            if (mentionsTL != null)
                foreach (var mention in mentionsTL)
                {
                    Console.WriteLine($@"
Processing:
    ID: {mention.Id}
    Text: {mention.Text}");

                    if (_previouslyProcessedTweets.Contains(mention.Id))
                    {
                        Console.WriteLine($"    {mention.Id} already processed");
                        continue; // already processed, move on
                    }
                    else
                        _previouslyProcessedTweets.Add(mention.Id);

                    var pattern = @"\#[0-9a-f]{3,6}";
                    var regex = new Regex(pattern, RegexOptions.IgnoreCase);

                    var match = regex.Match(mention.Text);

                    if (!match.Success)
                    {
                        Console.WriteLine("    No hex code found in {mention.Text}");

                        Tweet.PublishTweetInReplyTo($"@{mention.CreatedBy.ScreenName} couldn't find a hex code in your tweet, yo!", mention.Id);
                        return;
                    }

                    var hex = match.Value;

                    try
                    {
                        var imageFile = _generatedImagesFolder + "/" + $"{hex}.jpg";

                        if (!File.Exists(imageFile))
                        {
                            Console.WriteLine("File not generated previously, generating");
                            
                            using (FileStream outFileStream = File.OpenWrite(imageFile))
                            {
                                new Image(100, 100)
                                .BackgroundColor(new Color(hex))
                                .Save(outFileStream);
                            }
                        }
                        else
                        {
                            Console.WriteLine("    File already generated, returning from cache");
                        }

                        var attachment = File.ReadAllBytes(imageFile);
                        var media = Upload.UploadImage(attachment);

                        Tweet.PublishTweet($"@{mention.CreatedBy.ScreenName} Here you go!", new PublishTweetOptionalParameters
                        {
                            InReplyToTweetId = mention.Id,
                            Medias = new List<IMedia> { media }
                        });

                    }

                    catch (Exception ex)
                    {
                        // Our problem, allow for reprocess
                        _previouslyProcessedTweets.Remove(mention.Id);

                        Console.WriteLine(ex.StackTrace);
                        Tweet.PublishTweetInReplyTo($"@{mention.CreatedBy.ScreenName} Some problem occured. Sorry. I'll inform my master", mention.Id);
                    }

                    File.WriteAllText(_previouslyProcessedTweetsFile, JsonConvert.SerializeObject(_previouslyProcessedTweets));
                }
        }

        class Configuration
        {
            public string ConsumerKey {get; set;}
            public string ConsumerSecret {get; set;}
            public string UserAccessToken {get; set;}
            public string UserAccessSecret {get; set;}
        }

    }
}
