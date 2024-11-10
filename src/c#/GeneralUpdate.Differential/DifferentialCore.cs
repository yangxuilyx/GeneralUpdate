﻿using GeneralUpdate.Differential.Binary;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GeneralUpdate.Common.FileBasic;
using GeneralUpdate.Common.HashAlgorithms;

namespace GeneralUpdate.Differential
{
    public sealed class DifferentialCore
    {
        private static readonly object _lockObj = new ();
        private static DifferentialCore _instance;

        private const string PATCH_FORMAT = ".patch";
        private const string DELETE_FILES_NAME = "generalupdate_delete_files.json";

        public static DifferentialCore Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lockObj)
                    {
                        if (_instance == null)
                        {
                            _instance = new DifferentialCore();
                        }
                    }
                }
                return _instance;
            }
        }

        public async Task Clean(string sourcePath, string targetPath, string patchPath)
        {
            try
            {
                var fileManager = new GeneralFileManager();
                var comparisonResult = fileManager.Compare(sourcePath, targetPath);
                foreach (var file in comparisonResult.DifferentNodes)
                {
                    var tempDir = GetTempDirectory(file, targetPath, patchPath);
                    var oldFile = comparisonResult.LeftNodes.FirstOrDefault(i => i.Name.Equals(file.Name));
                    var newFile = file;

                    if (File.Exists(oldFile.FullName) && File.Exists(newFile.FullName) && string.Equals(oldFile.RelativePath, newFile.RelativePath))
                    {
                        if (!GeneralFileManager.HashEquals(oldFile.FullName, newFile.FullName))
                        {
                            var tempPatchPath = Path.Combine(tempDir, $"{file.Name}{PATCH_FORMAT}");
                            await new BinaryHandler().Clean(oldFile.FullName, newFile.FullName, tempPatchPath);
                        }
                    }
                    else
                    {
                        File.Copy(newFile.FullName, Path.Combine(tempDir, Path.GetFileName(newFile.FullName)), true);
                    }
                }

                var exceptFiles = fileManager.Except(sourcePath, targetPath);
                if (exceptFiles != null && exceptFiles.Any())
                {
                    var path = Path.Combine(patchPath, DELETE_FILES_NAME);
                    GeneralFileManager.CreateJson(path, exceptFiles);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Generate error : {ex.Message} !", ex.InnerException);
            }
        }

        public async Task Dirty(string appPath, string patchPath)
        {
            if (!Directory.Exists(appPath) || !Directory.Exists(patchPath)) return;

            try
            {
                var patchFiles = GeneralFileManager.GetAllfiles(patchPath);
                var oldFiles = GeneralFileManager.GetAllfiles(appPath);
                //Refresh the collection after deleting the file.
                HandleDeleteList(patchFiles, oldFiles);
                oldFiles = GeneralFileManager.GetAllfiles(appPath);
                foreach (var oldFile in oldFiles)
                {
                    var findFile = patchFiles.FirstOrDefault(f =>
                        Path.GetFileNameWithoutExtension(f.Name).Replace(PATCH_FORMAT, "").Equals(oldFile.Name));

                    if (findFile != null && Path.GetExtension(findFile.FullName).Equals(PATCH_FORMAT))
                    {
                        await DirtyPatch(oldFile.FullName, findFile.FullName);
                    }
                }

                await DirtyUnknow(appPath, patchPath);
            }
            catch (Exception ex)
            {
                throw new Exception($"Dirty error : {ex.Message} !", ex.InnerException);
            }
        }

        #region Private Methods

        private static string GetTempDirectory(FileNode file, string targetPath, string patchPath)
        {
            var tempPath = file.FullName.Replace(targetPath, "").Replace(Path.GetFileName(file.FullName), "").Trim(Path.DirectorySeparatorChar);
            var tempDir = string.IsNullOrEmpty(tempPath) ? patchPath : Path.Combine(patchPath, tempPath);
            Directory.CreateDirectory(tempDir);
            return tempDir;
        }

        private void HandleDeleteList(IEnumerable<FileInfo> patchFiles, IEnumerable<FileInfo> oldFiles)
        {
            var json = patchFiles.FirstOrDefault(i => i.Name.Equals(DELETE_FILES_NAME));
            if (json == null)
                return;
            
            var deleteFiles = GeneralFileManager.GetJson<IEnumerable<FileNode>>(json.FullName);
            if (deleteFiles == null)
                return;
            
            //Match the collection of files to be deleted based on the file hash values stored in the JSON file.
            var hashAlgorithm = new Sha256HashAlgorithm();
            var tempDeleteFiles = oldFiles.Where(old => deleteFiles.Any(del => del.Hash.SequenceEqual(hashAlgorithm.ComputeHash(old.FullName)))).ToList();
            foreach (var file in tempDeleteFiles)
            {
                if (File.Exists(file.FullName))
                {
                    File.Delete(file.FullName);
                }
            }
        }

        private async Task DirtyPatch(string appPath, string patchPath)
        {
            try
            {
                if (!File.Exists(appPath) || !File.Exists(patchPath))
                {
                    return;
                }

                var newPath = Path.Combine(Path.GetDirectoryName(appPath)!, $"{Path.GetRandomFileName()}_{Path.GetFileName(appPath)}");
                await new BinaryHandler().Dirty(appPath, newPath, patchPath);
            }
            catch (Exception ex)
            {
                throw new Exception($"RevertFile error : {ex.Message} !", ex.InnerException);
            }
        }

        private Task DirtyUnknow(string appPath, string patchPath)
        {
            try
            {
                var fileManager = new GeneralFileManager();
                var comparisonResult = fileManager.Compare(appPath, patchPath);
                foreach (var file in comparisonResult.DifferentNodes)
                {
                    var extensionName = Path.GetExtension(file.FullName);
                    if (BlackListManager.Instance.IsBlacklisted(extensionName)) continue;

                    var targetFileName = file.FullName.Replace(patchPath, "").TrimStart(Path.DirectorySeparatorChar);
                    var targetPath = Path.Combine(appPath, targetFileName);
                    var parentFolder = Directory.GetParent(targetPath);
                    if (parentFolder?.Exists == false)
                    {
                        parentFolder.Create();
                    }

                    File.Copy(file.FullName, targetPath, true);
                }

                if (Directory.Exists(patchPath))
                {
                    Directory.Delete(patchPath, true);
                }
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                throw new Exception($"DirtyNew error : {ex.Message} !", ex.InnerException);
            }
        }

        #endregion
    }
}