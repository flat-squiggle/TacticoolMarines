using System;
using System.Collections.Generic;

namespace CharacterReplacement
{
    /// <summary>Chat help: /tmhelp for the overview, /tmhelp &lt;topic&gt; for the details of one area.</summary>
    internal static class Help
    {
        private static readonly (string key, string title, string[] lines)[] Topics =
        {
            ("soldiers", "Soldiers", new[]
            {
                "/tminfo              status: how many soldiers, how many revived",
                "/tmspawn             spawn a soldier right in front of you",
                "/tmlook              re-roll the look of all soldiers",
                "/tmremove            overview and removal commands (see /tmhelp cleanup)",
            }),
            ("work", "Work and orders", new[]
            {
                "/tmjobs              auto jobs on/off and their settings",
                "/tmbuild             tell everyone: finish buildings",
                "/tmmaintain          tell everyone: maintain the base",
                "/tmclear             tell everyone: clear the area",
                "/tmget               tell everyone: fetch materials",
                "/tmfollow            tell everyone: follow",
                "/tmstay              tell everyone: stay here",
                "/tmbreaks on|off     no breaks - they keep working",
                "Note: /tmfollow and /tmstay pause the auto jobs until you give",
                "a work order. After that the auto jobs take over again.",
            }),
            ("ranges", "Search ranges", new[]
            {
                "/tmrange <number>    radius for trees and materials lying around",
                "/tmstruct <number>   radius for build sites, holders and repairs",
                "Both apply at once, without a restart. 1 is the game's value,",
                "2 doubles the radius. Very large values cost server performance.",
            }),
            ("cleanup", "Cleanup", new[]
            {
                "/tmclean on|off|now  remove the bodies of killed soldiers",
                "/tmclean <seconds>   how long a body stays",
                "/tmremove            overview: total / managed / surplus",
                "/tmremove dupes      remove only the surplus ones",
                "/tmremove <count>    remove that many, farthest first",
                "/tmremove <name>     remove one specific soldier",
                "/tmremove all        remove all soldiers",
                "Removing is permanent - they stay gone after a restart too.",
                "The story Kelvin is never removed.",
            }),
            ("measuring", "Measuring performance", new[]
            {
                "/tmperf              server FPS, CPU, RAM, network, number of AI characters",
                "/tmprofile           breakdown of one server frame in milliseconds",
                "Both measure in one-minute windows - right after a start",
                "there is nothing to show yet.",
            }),
        };

        public static IEnumerable<string> Lines(string topic)
        {
            topic = (topic ?? "").Trim().ToLowerInvariant();

            if (topic.Length > 0)
            {
                foreach (var t in Topics)
                {
                    if (t.key != topic) continue;
                    yield return $"--- {t.title} ---";
                    foreach (var l in t.lines) yield return l;
                    yield break;
                }
                yield return $"No help topic '{topic}'.";
            }

            yield return "Tacticool Marines - commands. Details with /tmhelp <topic>:";
            foreach (var t in Topics) yield return $"  /tmhelp {t.key,-12} {t.title}";
            yield return "Used most: /tminfo, /tmjobs, /tmbuild, /tmremove, /tmperf";
        }
    }
}
