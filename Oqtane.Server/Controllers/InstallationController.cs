using System;
using System.IO;
using System.Reflection;
using System.Linq;
using System.IO.Compression;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Oqtane.Infrastructure;
using Oqtane.Models;
using Oqtane.Modules;
using Oqtane.Shared;
using Oqtane.Themes;
using Microsoft.Extensions.Caching.Memory;
using System.Net;
using Oqtane.Repository;
using Microsoft.AspNetCore.Http;

namespace Oqtane.Controllers
{
    [Route(ControllerRoutes.ApiRoute)]
    public class InstallationController : Controller
    {
        private readonly IConfigurationRoot _config;
        private readonly IInstallationManager _installationManager;
        private readonly IDatabaseManager _databaseManager;
        private readonly ILocalizationManager _localizationManager;
        private readonly IMemoryCache _cache;
        private readonly IHttpContextAccessor _accessor;
        private readonly IAliasRepository _aliases;

        public InstallationController(IConfigurationRoot config, IInstallationManager installationManager, IDatabaseManager databaseManager, ILocalizationManager localizationManager, IMemoryCache cache, IHttpContextAccessor accessor, IAliasRepository aliases)
        {
            _config = config;
            _installationManager = installationManager;
            _databaseManager = databaseManager;
            _localizationManager = localizationManager;
            _cache = cache;
            _accessor = accessor;
            _aliases = aliases;
        }

        // POST api/<controller>
        [HttpPost]
        public Installation Post([FromBody] InstallConfig config)
        {
            var installation = new Installation {Success = false, Message = ""};

            if (ModelState.IsValid && (User.IsInRole(RoleNames.Host) || string.IsNullOrEmpty(_config.GetConnectionString(SettingKeys.ConnectionStringKey))))
            {
                installation = _databaseManager.Install(config);
            }
            else
            {
                installation.Message = "Installation Not Authorized";
            }

            return installation;
        }

        // GET api/<controller>/installed/?path=xxx
        [HttpGet("installed")]
        public Installation IsInstalled(string path)
        {
            var installation = _databaseManager.IsInstalled();
            if (installation.Success)
            {
                path = _accessor.HttpContext.Request.Host.Value + "/" + WebUtility.UrlDecode(path);
                installation.Alias = _aliases.GetAlias(path);
            }
            return installation;
        }

        [HttpGet("upgrade")]
        [Authorize(Roles = RoleNames.Host)]
        public Installation Upgrade()
        {
            var installation = new Installation {Success = true, Message = ""};
            _installationManager.UpgradeFramework();
            return installation;
        }

        // GET api/<controller>/restart
        [HttpPost("restart")]
        [Authorize(Roles = RoleNames.Host)]
        public void Restart()
        {
            _installationManager.RestartApplication();
        }

        // GET api/<controller>/load
        [HttpGet("load")]
        public IActionResult Load()
        {
            if (_config.GetSection("Runtime").Value == "WebAssembly")
            {
                return File(GetAssemblies(), System.Net.Mime.MediaTypeNames.Application.Octet, "oqtane.dll");
            }
            else
            {
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return null;
            }
        }

        private byte[] GetAssemblies()
        {            
            return _cache.GetOrCreate("assemblies", entry =>
            {
                // get list of assemblies which should be downloaded to client
                var assemblies = AppDomain.CurrentDomain.GetOqtaneClientAssemblies();
                var list = assemblies.Select(a => a.GetName().Name).ToList();
                var binFolder = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);

                // insert satellite assemblies at beginning of list
                foreach (var culture in _localizationManager.GetSupportedCultures())
                {
                    var assembliesFolderPath = Path.Combine(binFolder, culture);
                    if (culture == Constants.DefaultCulture)
                    {
                        continue;
                    }

                    if (Directory.Exists(assembliesFolderPath))
                    {
                        foreach (var resourceFile in Directory.EnumerateFiles(assembliesFolderPath))
                        {
                            list.Insert(0, Path.Combine(culture, Path.GetFileNameWithoutExtension(resourceFile)));
                        }
                    }
                    else
                    {
                        Console.WriteLine($"The satellite assemblies folder named '{culture}' is not found.");
                    }
                }

                // insert module and theme dependencies at beginning of list
                foreach (var assembly in assemblies)
                {
                    foreach (var type in assembly.GetTypes().Where(item => item.GetInterfaces().Contains(typeof(IModule))))
                    {
                        var instance = Activator.CreateInstance(type) as IModule;
                        foreach (string name in instance.ModuleDefinition.Dependencies.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (System.IO.File.Exists(Path.Combine(binFolder, name + ".dll")))
                            {
                                if (!list.Contains(name)) list.Insert(0, name);
                            }
                            else
                            {
                                Console.WriteLine("Module " + instance.ModuleDefinition.ModuleDefinitionName + " dependency " + name + ".dll does not exist");
                            }
                        }
                    }
                    foreach (var type in assembly.GetTypes().Where(item => item.GetInterfaces().Contains(typeof(ITheme))))
                    {
                        var instance = Activator.CreateInstance(type) as ITheme;
                        foreach (string name in instance.Theme.Dependencies.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (System.IO.File.Exists(Path.Combine(binFolder, name + ".dll")))
                            {
                                if (!list.Contains(name)) list.Insert(0, name);
                            }
                            else
                            {
                                Console.WriteLine("Theme " + instance.Theme.ThemeName + " dependency " + name + ".dll does not exist" );
                            }
                        }
                    }
                }

                // create zip file containing assemblies and debug symbols
                using (var memoryStream = new MemoryStream())
                {
                    using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
                    {
                        foreach (string file in list)
                        {
                            using (var filestream = new FileStream(Path.Combine(binFolder, file + ".dll"), FileMode.Open, FileAccess.Read))
                            using (var entrystream = archive.CreateEntry(file + ".dll").Open())
                            {
                                filestream.CopyTo(entrystream);
                            }

                            // include debug symbols
                            if (System.IO.File.Exists(Path.Combine(binFolder, file + ".pdb")))
                            {
                                using (var filestream = new FileStream(Path.Combine(binFolder, file + ".pdb"), FileMode.Open, FileAccess.Read))
                                using (var entrystream = archive.CreateEntry(file + ".pdb").Open())
                                {
                                    filestream.CopyTo(entrystream);
                                }
                            }
                        }
                    }

                    return memoryStream.ToArray();
                }
            });
        }
    }
}
