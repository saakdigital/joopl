﻿// (c) joopl 
// By Matías Fidemraizer (http://www.matiasfidemraizer.com) (http://www.linkedin.com/in/mfidemraizer/en)
// -------------------------------------------------
// Project site on GitHub: http://mfidemraizer.github.io/joopl/
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using System.Text;

namespace joopl.DependencyBuilder
{
    public sealed class DependencyBuilder
    {
        public List<FileManifest> BuildDependencyUsageMap(List<Namespace> dependencyMap, string baseDirectory, string[] excludeFiles = null)
        {
            IEnumerable<IDictionary<string, object>> thirdPartyDependencies = null;

            if (File.Exists(Path.Combine(baseDirectory, "ThirdPartyDependencies.json")))
            {
                thirdPartyDependencies = JsonConvert.DeserializeObject<List<ExpandoObject>>(File.ReadAllText(Path.Combine(baseDirectory, "ThirdPartyDependencies.json")));
            }

            List<KeyValuePair<string, string>> tokens;
            int tokenIndex = 0;
            string relativeFilePath = null;

            IEnumerable<FileInfo> files = new DirectoryInfo(baseDirectory).GetFiles("*.js", SearchOption.AllDirectories);

            List<Namespace> namespaces = new List<Namespace>();
            List<string> scopeNamespaces = new List<string>();

            FileManifest fileManifest;
            List<FileManifest> usageMap = new List<FileManifest>();

            JsParser jsParser = new JsParser();
            IEnumerable<Type> allTypes = dependencyMap.SelectMany(ns => ns.Members);

            foreach (FileInfo file in files)
            {
                if (excludeFiles != null && excludeFiles.Contains(file.Name))
                {
                    continue;
                }

                tokens = jsParser.ParseTokens(file.FullName);
                relativeFilePath = file.FullName.Replace(baseDirectory, string.Empty).Replace('\\', '/').TrimStart('/');
                fileManifest = new FileManifest();

                while (tokenIndex < tokens.Count)
                {
                    if (tokens[tokenIndex].Value == "$namespace" && new[] { "register", "using" }.Any(token => token == tokens[tokenIndex + 2].Value))
                    {
                        if (tokens[tokenIndex + 2].Value == "using" && tokens[tokenIndex + 4].Value == "[")
                        {
                            IEnumerable<string> namespaceDeclaration = tokens.Skip(tokenIndex + 5).Select(token => token.Value);
                            int indexOfEnd = Array.IndexOf(namespaceDeclaration.ToArray(), "]");
                            List<string> remainingTokens = namespaceDeclaration.Take(indexOfEnd).ToList();

                            for (int namespaceIndex = 0; namespaceIndex < remainingTokens.Count(); namespaceIndex++)
                            {
                                if (remainingTokens[namespaceIndex] != ",")
                                {
                                    scopeNamespaces.Add(remainingTokens[namespaceIndex]);
                                }
                            }
                        }
                        else
                        {
                            scopeNamespaces.Add(tokens[tokenIndex + 4].Value);
                        }

                        if (scopeNamespaces.Count > 0)
                        {
                            foreach (string someNs in scopeNamespaces)
                            {
                                if (!namespaces.Any(some => some.Name == someNs))
                                {
                                    namespaces.Add(new Namespace { Name = someNs, Files = new List<string> { relativeFilePath } });
                                }
                            }
                        }
                    }
                    else if (scopeNamespaces.Count > 0)
                    {
                        if (tokens[tokenIndex].Value == "$extends")
                        {
                            string memberName = null;
                            bool withNs = false;

                            if (tokens[tokenIndex + 2].Value == "$global")
                            {
                                withNs = true;

                                memberName = string.Join
                                            (
                                                string.Empty,
                                                tokens.Skip(tokenIndex + 4)
                                                    .TakeWhile(someToken => someToken.Value != "," && someToken.Value != ";")
                                                    .Select(someToken => someToken.Value)
                                                    .ToArray()
                                            );
                            }
                            else
                            {
                                memberName = tokens[tokenIndex + 4].Value;
                            }

                            IEnumerable<Type> mappedMembers = dependencyMap.SelectMany(mappedNs => mappedNs.Members);

                            Type scopedMember;

                            if (withNs)
                            {
                                string[] memberPath = memberName.Split('.');
                                string scopedNs = string.Join(string.Empty, memberPath.Take(memberPath.Length - 1).ToArray());

                                memberName = memberPath.Last();

                                scopedMember = mappedMembers.FirstOrDefault
                                (
                                    member => member.Name == memberName && member.Parent.Name == scopedNs
                                );
                            }
                            else
                            {
                                scopedMember = mappedMembers.FirstOrDefault
                                (
                                    member => member.Name == memberName && scopeNamespaces.Any(scopedNs => scopedNs == member.Namespace)
                                );
                            }

                            if (scopedMember != null)
                            {
                                if (
                                    (scopedMember.FileName != "joopl.js" && scopedMember.FileName != "joopl.min.js")
                                    && !scopedMember.FileName.Contains(file.Name)
                                    && fileManifest.DependendsOn.Count(fileName => fileName == scopedMember.FileName) == 0
                                )
                                {
                                    fileManifest.DependendsOn.Insert(0, scopedMember.FileName);
                                }
                            }
                        }
                        else if (thirdPartyDependencies != null && thirdPartyDependencies.Count() > 0 && tokens[tokenIndex].Value.StartsWith("use "))
                        {
                            if (fileManifest.Libraries == null)
                            {
                                fileManifest.Libraries = new List<string>();
                            }

                            string dependencyName = tokens[tokenIndex].Value.Split(' ').Last();

                            fileManifest.Libraries.Add((string)thirdPartyDependencies.Single(some => (string)some["name"] == dependencyName)["uri"]);

                        }
                        else
                        {
                            Type scopedMember = allTypes.SingleOrDefault
                            (
                                type => scopeNamespaces.Any(ns => ns == type.Namespace) && type.Name == tokens[tokenIndex].Value
                            );

                            if (scopedMember == null)
                            {
                                int tokenSearchIndex = tokenIndex - 1;
                                string[] endOfSearchTokens = new string[] { "{", "}", "=", ";", "(", ")" };
                                StringBuilder fullNs = new StringBuilder();
                                bool end = false;

                                while (!end && tokenSearchIndex >= 0 && !endOfSearchTokens.Contains(tokens[tokenSearchIndex].Value))
                                {
                                    if (tokens[tokenSearchIndex].Value != "$global")
                                    {
                                        fullNs.Append(tokens[tokenSearchIndex].Value);
                                    }

                                    tokenSearchIndex--;
                                }

                                string foundNs = fullNs.ToString().Trim('.');

                                scopedMember = allTypes.SingleOrDefault
                                (
                                    type => type.Namespace == foundNs && type.Name == tokens[tokenIndex].Value
                                );
                            }

                            if (scopedMember != null)
                            {
                                if (
                                    (scopedMember.FileName != "joopl.js" && scopedMember.FileName != "joopl.min.js")
                                    && !scopedMember.FileName.Contains(file.Name)
                                    && fileManifest.DependendsOn.Count(fileName => fileName == scopedMember.FileName) == 0
                                )
                                {
                                    fileManifest.DependendsOn.Add(scopedMember.FileName);
                                }
                            }
                        }
                    }

                    tokenIndex++;
                }

                fileManifest.FileName = relativeFilePath;

                if (fileManifest.DependendsOn.Count == 0)
                {
                    fileManifest.DependendsOn = null;
                }

                usageMap.Add(fileManifest);

                scopeNamespaces.Clear();
                tokenIndex = 0;
            }

            foreach (FileManifest manifest in usageMap)
            {
                if (manifest.DependendsOn != null)
                {
                    manifest.DependendsOn = GetDependencies(usageMap, manifest);
                    manifest.DependendsOn.Reverse();
                }
            }

            return usageMap;
        }

        private List<string> GetDependencies(IEnumerable<FileManifest> usageMap, FileManifest parentManifest = null, List<string> dependencies = null)
        {
            dependencies = dependencies ?? new List<string>();

            if (parentManifest == null)
            {
                foreach (FileManifest manifest in usageMap)
                {
                    if (manifest.DependendsOn.Count > 0)
                    {
                        foreach (string fileName in manifest.DependendsOn)
                        {
                            if ((fileName != manifest.FileName && fileName != "joopl.js" && fileName != "joopl.min.js") && !dependencies.Contains(fileName))
                            {
                                dependencies.Add(fileName);

                                GetDependencies(usageMap, usageMap.Single(some => some.FileName == fileName), dependencies);
                            }
                        }
                    }
                }
            }
            else if (parentManifest.DependendsOn != null && parentManifest.DependendsOn.Count > 0)
            {
                foreach (string fileName in parentManifest.DependendsOn)
                {
                    if ((fileName != parentManifest.FileName && fileName != "joopl.js" && fileName != "joopl.min.js") && !dependencies.Contains(fileName))
                    {
                        dependencies.Add(fileName);
                        dependencies.Reverse();
                        GetDependencies(usageMap, usageMap.Single(some => some.FileName == fileName), dependencies);
                    }
                }
            }

            return dependencies;
        }

        public List<Namespace> BuildDependencyMap(string baseDirectory, string[] excludeFiles = null)
        {
            List<KeyValuePair<string, string>> tokens;
            int tokenIndex = 0;
            string currentNs = null;
            Namespace ns = null;
            string relativeFilePath = null;

            IEnumerable<FileInfo> files = new DirectoryInfo(baseDirectory).GetFiles("*.js", SearchOption.AllDirectories);

            List<Namespace> namespaces = new List<Namespace>();

            JsParser jsParser = new JsParser();

            foreach (FileInfo file in files)
            {
                if (excludeFiles != null && excludeFiles.Contains(file.Name))
                {
                    continue;
                }

                tokens = jsParser.ParseTokens(file.FullName);
                relativeFilePath = file.FullName.Replace(baseDirectory, string.Empty).Replace('\\', '/').TrimStart('/');

                while (tokenIndex < tokens.Count)
                {
                    if (tokens[tokenIndex].Value == "$namespace" && tokens[tokenIndex + 2].Value == "register")
                    {
                        currentNs = tokens[tokenIndex + 4].Value;

                        if (namespaces.Count(some => some.Name == currentNs) == 0)
                        {
                            ns = new Namespace { Name = currentNs };
                            namespaces.Add(ns);
                        }
                        else
                        {
                            ns = namespaces.Single(some => some.Name == currentNs);
                        }

                        if (!ns.Files.Any(someFile => someFile == relativeFilePath))
                        {
                            ns.Files.Add(relativeFilePath);
                        }

                        tokenIndex += 4;
                    }
                    else if (new[] { "$def", "$enumdef" }.Any(token => token == tokens[tokenIndex].Value))
                    {
                        if (ns != null && new Regex("^([A-Z][A-Za-z0-9]+)$").IsMatch(tokens[tokenIndex - 2].Value))
                        {
                            ns.Members.Add(new Type { Parent = ns, FileName = relativeFilePath, Name = tokens[tokenIndex - 2].Value });
                        }

                        tokenIndex++;
                    }
                    else
                    {
                        tokenIndex++;
                    }
                }

                tokenIndex = 0;
            }

            return namespaces;
        }
    }
}