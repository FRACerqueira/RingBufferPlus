using XmlDocMarkdown.Core;

namespace XmlDocMarkdownGenerator
{
    internal class Program
    {
        //perform XmlDoc Markdown Generator
        static int Main()
        {
            if (Directory.Exists(@"..\..\..\..\docs\assemblies"))
            {
                Directory.Delete(@"..\..\..\..\docs\assemblies", true);
            }

            var args = new string[] { "RingBufferPlus", @"..\..\..\..\docs\assemblies" };
            XmlDocMarkdownApp.Run(args);

            //create custom header and footer for namespaces
            var files = Directory.GetFiles(@"..\..\..\..\docs\assemblies", "*.md");
            foreach (var item in files)
            {
                var content = File.ReadAllLines(item).ToList();
                var title = content[0].Replace("# ", "");
                content[0]= "![RingBufferPlus Logo](https://raw.githubusercontent.com/FRACerqueira/RingBufferPlus/refs/heads/main/icon.png)";
                content[1]= "";
                content.Insert(2,$"### {title}");
                content.Insert(3, "</br>");
                content.Insert(4, "");
                var line = 5;
                while (!content[line].Contains("<!-- DO NOT EDIT"))
                {
                    if (content[line].StartsWith("## "))
                    {
                        content[line] = content[line].Replace("## ", "### ");
                        line++;
                    }
                    else if (content[line].StartsWith("# "))
                    {
                        content[line] = content[line].Replace("# ", "### ");
                        line++;
                    }
                    else
                    {
                        line++;
                    }
                }
                content[line] = "### See Also";
                content.Add("* [Main Index](../docindex.md)");
                File.Delete(item);
                File.WriteAllLines(item, content);
            }

            var folders = Directory.GetDirectories(@"..\..\..\..\docs\assemblies");
            foreach (var item in folders)
            {
                var itemsfolder = Directory.GetDirectories(item);
                foreach (var item1 in itemsfolder)
                {
                    var itemsfile = Directory.GetFiles(item1, "*.md");
                    foreach (var item2 in itemsfile)
                    {
                        var content = File.ReadAllLines(item2).ToList();
                        var title = content[0].Replace("# ", "");
                        content.Insert(0, "![RingBufferPlus Logo](https://raw.githubusercontent.com/FRACerqueira/RingBufferPlus/refs/heads/main/icon.png)");
                        content[1] = "";
                        content.Insert(2, $"### {title}");
                        content.Insert(3, "</br>");
                        content.Insert(4, "");
                        var line = 5;
                        var foundsubtitle = false;
                        while (!content[line].Contains("<!-- DO NOT EDIT"))
                        {
                            if (content[line] == "---")
                            {
                                foundsubtitle = false;
                            }
                            else if (content[line].Length > 0 && !content[line].StartsWith("#") && !foundsubtitle)
                            {
                                content[line] = $"#### {content[line]}";
                                foundsubtitle = true;
                            }
                            else if (content[line].StartsWith("## "))
                            {
                                foundsubtitle = true;
                                content[line] = content[line].Replace("## ", "### ");
                            }
                            else if (content[line].StartsWith("# "))
                            {
                                content[line] = content[line].Replace("# ", "### ");
                            }
                            line++;
                        }
                        content.Add("* [Main Index](../../../docindex.md)");
                        File.Delete(item2);
                        File.WriteAllLines(item2, content);
                    }
                }
                var itemsfiles = Directory.GetFiles(item);
                foreach (var item3 in itemsfiles)
                {
                    var content = File.ReadAllLines(item3).ToList();
                    var title = content[0].Replace("# ", "");
                    content.Insert(0, "![RingBufferPlus Logo](https://raw.githubusercontent.com/FRACerqueira/RingBufferPlus/refs/heads/main/icon.png)");
                    content[1] = "";
                    content.Insert(2, $"### {title}");
                    content.Insert(3, "</br>");
                    content.Insert(4, "");
                    var line = 5;
                    var foundsubtitle = false;
                    while (!content[line].Contains("<!-- DO NOT EDIT"))
                    {
                        if (content[line] == "---")
                        {
                            foundsubtitle = false;
                        }
                        else if (content[line].Length > 0 && !content[line].StartsWith("#") && !foundsubtitle)
                        {
                            content[line] = $"#### {content[line]}";
                            foundsubtitle = true;
                        }
                        else if (content[line].StartsWith("## "))
                        {
                            foundsubtitle = true;
                            content[line] = content[line].Replace("## ", "### ");
                        }
                        else if (content[line].StartsWith("# "))
                        {
                            content[line] = content[line].Replace("# ", "### ");
                        }
                        line++;
                    }
                    content.Add("* [Main Index](../../docindex.md)");
                    File.Delete(item3);
                    File.WriteAllLines(item3, content);
                }
            }
            return 0;
        }
    }
}
