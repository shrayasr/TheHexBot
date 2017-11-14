using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
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

        private static bool _firstRun = false;
        private static int _tweetsPerPage = 5;

        private static bool _isDryRun = false;

        public static void Main(string[] cmdlineArgs)
        {
            if (!File.Exists(_configurationFile))
            {
                Console.WriteLine("No configuration found. Can't continue");
                return;
            }

            if (cmdlineArgs.Contains("--dryrun"))
                _isDryRun = true;

            Console.WriteLine("Dry run: " + _isDryRun);

            _configuration = JsonConvert.DeserializeObject<Configuration>(File.ReadAllText(_configurationFile));

            if (_configuration.CacheDir != null && _configuration.CacheDir.Trim().Length > 0)
                _generatedImagesFolder = _configuration.CacheDir;

            if (!Directory.Exists(_generatedImagesFolder))
                Directory.CreateDirectory(_generatedImagesFolder);

            if (File.Exists(_previouslyProcessedTweetsFile))
                _previouslyProcessedTweets = JsonConvert.DeserializeObject<List<long>>(File.ReadAllText(_previouslyProcessedTweetsFile));
            else
                _firstRun = true;

            Auth.SetUserCredentials(
                _configuration.ConsumerKey,
                _configuration.ConsumerSecret,
                _configuration.UserAccessToken,
                _configuration.UserAccessSecret);

            RateLimit.RateLimitTrackerMode = RateLimitTrackerMode.TrackOnly;

            TweetinviEvents.QueryBeforeExecute += (sender, args) =>
            {
                var queryRateLimits = RateLimit.GetQueryRateLimit(args.QueryURL);

                if (queryRateLimits != null)
                {
                    if (queryRateLimits.Remaining > 0)
                    {
                        // process
                        return;
                    }

                    args.Cancel = true;
                    System.Console.WriteLine("ERROR: Rate limit hit\nQuery: " + args.QueryURL);
                }
            };

            Console.WriteLine(DateTime.Now);

            var firstPage = true;
            var pageNo = 1;
            while (true)
            {
                System.Console.WriteLine("Page: " + pageNo++);

                var mentionTLParams = new MentionsTimelineParameters
                {
                    MaximumNumberOfTweetsToRetrieve = _tweetsPerPage
                };

                if (!_firstRun)
                    mentionTLParams.SinceId = _previouslyProcessedTweets.Max();

                if (!firstPage)
                    mentionTLParams.MaxId = _previouslyProcessedTweets.Min() - 1;

                var mentionsTLPage = Timeline.GetMentionsTimeline(mentionTLParams);

                if (mentionsTLPage == null || mentionsTLPage.Count() == 0)
                {
                    System.Console.WriteLine("No mentions to process");
                    break;
                }
                else
                {
                    foreach (var mention in mentionsTLPage)
                    {
                        if (_isDryRun)
                            ShowMention(mention);
                        else
                            ProcessMention(mention);
                    }
                }

                if (firstPage)
                    firstPage = false;
            }

            File.WriteAllText(_previouslyProcessedTweetsFile, JsonConvert.SerializeObject(_previouslyProcessedTweets));
        }

        private static void ShowMention(IMention mention)
        {
            System.Console.WriteLine("");
            System.Console.WriteLine("ID: " + mention.Id);
            System.Console.WriteLine("Text: " + mention.Text);
            System.Console.WriteLine("Has Quoted tweet: " + (mention.QuotedTweet != null));
            System.Console.WriteLine("Quoted ID: " + mention.QuotedTweet?.Id);
            System.Console.WriteLine("Quoted Text: " + mention.QuotedTweet?.Text);
            System.Console.WriteLine("");

            _previouslyProcessedTweets.Add(mention.Id);
        }

        private static void ProcessMention(IMention mention)
        {
            Console.WriteLine($@"
Processing:
    ID: {mention.Id}
    Text: {mention.Text}");

            if (_previouslyProcessedTweets.Contains(mention.Id))
            {
                Console.WriteLine($"    {mention.Id} already processed");
                return; // already processed, move on
            }
            else
                _previouslyProcessedTweets.Add(mention.Id);

            var pattern = @"\#?[0-9a-f]{3,6}";
            var regex = new Regex(pattern, RegexOptions.IgnoreCase);

            var textToMatch = mention.Text;

            if (mention.QuotedTweet != null)
                textToMatch = mention.QuotedTweet.Text;

            var match = regex.Match(textToMatch);

            if (!match.Success)
            {
                Console.WriteLine("    No hex code found in {mention.Text}");

                Tweet.PublishTweetInReplyTo($"@{mention.CreatedBy.ScreenName} couldn't find a hex code in your tweet, yo!", mention.Id);
                return;
            }

            var hex = match.Value;

            if (hex.StartsWith("#"))
                hex = hex.Substring(1);

            try
            {
                var imageFile = Path.Combine(_generatedImagesFolder, $"{hex}.jpg");

                if (!File.Exists(imageFile))
                {
                    Console.WriteLine("File not generated previously, generating");

                    using (Image<Rgba32> image = new Image<Rgba32>(100, 100))
                    {
                        image
                            .Mutate(ctx => ctx.BackgroundColor(Rgba32.FromHex(hex)));

                        image.Save(imageFile);
                    }
                }
                else
                {
                    Console.WriteLine("    File already generated, returning from cache");
                }

                var attachment = File.ReadAllBytes(imageFile);
                var media = Upload.UploadImage(attachment);

                string mentionedNames = "@"+mention.CreatedBy.ScreenName+" ";
                foreach (var name in mention.UserMentions)
                {
                    // skip the bot, because it will be tweet itself
                    if (name.ScreenName != "thehexbot")
                    {
                        mentionedNames += "@"+name.ScreenName+" ";
                    }
                }

                Tweet.PublishTweet($"{mentionedNames} Found '{hex}' in your tweet. Here you go!", new PublishTweetOptionalParameters
                        {
                            InReplyToTweetId = mention.Id,
                            Medias = new List<IMedia> { media }
                        });

                Console.WriteLine("    Replied!");
            }

            catch (Exception ex)
            {
                // Our problem, allow for reprocess
                _previouslyProcessedTweets.Remove(mention.Id);

                Console.WriteLine(ex.StackTrace);
                Tweet.PublishTweetInReplyTo($"@{mention.CreatedBy.ScreenName} Some problem occured. Sorry. I'll inform my master", mention.Id);
            }
        }

        class Configuration
        {
            public string ConsumerKey { get; set; }
            public string ConsumerSecret { get; set; }
            public string UserAccessToken { get; set; }
            public string UserAccessSecret { get; set; }
            public string CacheDir { get; set; }
        }

    }
}
