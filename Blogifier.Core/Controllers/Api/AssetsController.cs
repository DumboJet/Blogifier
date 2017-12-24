﻿using Blogifier.Core.Common;
using Blogifier.Core.Data.Domain;
using Blogifier.Core.Data.Interfaces;
using Blogifier.Core.Data.Models;
using Blogifier.Core.Services.FileSystem;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Blogifier.Core.Controllers.Api
{
    [Authorize]
    [Route("blogifier/api/[controller]")]
    public class AssetsController : Controller
    {
        IUnitOfWork _db;
        ILogger _logger;

        public AssetsController(IUnitOfWork db, ILogger<AssetsController> logger)
        {
            _db = db;
            _logger = logger;
        }

        // GET: api/assets/2?search=foo&filter=filterImages
        [HttpGet]
        public async Task<AdminAssetList> Get(string filter, string search = "", int page = 1)
        {
            var profile = await GetProfile();
            var pager = new Pager(page);
            IEnumerable<Asset> assets;

            var term = search == null || search == "null" ? "" : search;
            var fltr = filter == null || filter == "null" ? "" : filter;

            if (filter == "filterImages")
            {
                assets = await _db.Assets.Find(a => a.ProfileId == profile.Id && a.Title.Contains(term) && a.AssetType == AssetType.Image, pager);
            }
            else if (filter == "filterAttachments")
            {
                assets = await _db.Assets.Find(a => a.ProfileId == profile.Id && a.Title.Contains(term) && a.AssetType == AssetType.Attachment, pager);
            }
            else
            {
                assets = await _db.Assets.Find(a => a.ProfileId == profile.Id && a.Title.Contains(term), pager);
            }

            return new AdminAssetList { Assets = assets, Pager = pager };
        }

        // GET api/assets/asset/5
        [HttpGet("asset/{id:int}")]
        public async Task<Asset> GetSingle(int id)
        {
            var model = await _db.Assets.Single(a => a.Id == id);
            return model;
        }

        // GET: api/assets/profilelogo/3
        [HttpGet]
        [Route("{type}/{id:int}")]
        public async Task<Asset> UpdateProfileImage(string type, int id)
        {
            var asset = await _db.Assets.Single(a => a.Id == id);
            type = type.ToLower();

            if (!string.IsNullOrEmpty(type))
            {
                if (type == "applogo")
                {
                    BlogSettings.Logo = asset.Url;
                    await _db.CustomFields.SetCustomField(CustomType.Application, 0, Constants.ProfileLogo, asset.Url);
                }
                else if (type == "appavatar")
                {
                    ApplicationSettings.ProfileAvatar = asset.Url;
                    await _db.CustomFields.SetCustomField(CustomType.Application, 0, Constants.ProfileAvatar, asset.Url);
                }
                else if (type == "appimage")
                {
                    BlogSettings.Cover = asset.Url;
                    await _db.CustomFields.SetCustomField(CustomType.Application, 0, Constants.ProfileImage, asset.Url);
                }
                else if (type == "apppostimage")
                {
                    BlogSettings.PostCover = asset.Url;
                    await _db.CustomFields.SetCustomField(CustomType.Application, 0, Constants.PostImage, asset.Url);
                }
                else
                {
                    var profile = await GetProfile();
                    if (type == "profilelogo")
                    {
                        profile.Logo = asset.Url;
                    }
                    if (type == "profileavatar")
                    {
                        profile.Avatar = asset.Url;
                    }
                    if (type == "profileimage")
                    {
                        profile.Image = asset.Url;
                    }
                }
                await _db.Complete();
            }
            return asset;
        }

        // GET: api/assets/postimage/3/5
        [HttpGet]
        [Route("postimage/{assetId:int}/{postId:int}")]
        public async Task<Asset> UpdatePostImage(string type, int assetId, int postId)
        {
            var asset = await _db.Assets.Single(a => a.Id == assetId);
            if (postId > 0)
            {
                var post = await _db.BlogPosts.Single(p => p.Id == postId);
                post.Image = asset.Url;
                await _db.Complete();
            }
            return asset;
        }

        // POST: api/assets/upload
        [HttpPost]
        [Route("upload")]
        public async Task<ActionResult> TinyMceUpload(IFormFile file)
        {
            var asset = await SaveFile(file);
            var location = asset.Url;
            return Json(new { location });
        }

        // POST api/assets/multiple
        [HttpPost]
        [Route("multiple")]
        public async Task<IActionResult> Multiple(ICollection<IFormFile> files)
        {
            foreach (var file in files)
            {
                await SaveFile(file);
            }
            return Ok("Created");
        }

        // DELETE api/assets/5
        [HttpDelete("{id:int}")]
        public async Task<IActionResult> Delete(int id)
        {
            var asset = await _db.Assets.Single(a => a.Id == id);
            if (asset == null)
                return NotFound();

            var blog = await GetProfile();
            try
            {
                var storage = new BlogStorage(blog.Slug);
                storage.DeleteFile(asset.Path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
            }

            _db.Assets.Remove(asset);
            await _db.Complete();

            // reset profile image to default
            // if asset was removed
            var profiles = await _db.Profiles.Where(p => p.Image == asset.Url || p.Avatar == asset.Url || p.Logo == asset.Url).ToListAsync();
            if (profiles != null)
            {
                foreach (var item in profiles)
                {
                    if (item.Image == asset.Url) item.Image = null;
                    if (item.Avatar == asset.Url) item.Avatar = null;
                    if (item.Logo == asset.Url) item.Logo = null;
                    await _db.Complete();
                }
            }
            return new NoContentResult();
        }

        // DELETE api/assets/profilelogo
        [HttpDelete("{type}")]
        public async Task<IActionResult> Delete(string type)
        {
            var profile = await GetProfile();

            if (type == "profileAvatar")
                profile.Avatar = null;

            if (type == "profileLogo")
                profile.Logo = null;

            if (type == "profileImage")
                profile.Image = null;

            await _db.Complete();
            return new NoContentResult();
        }

        // DELETE api/assets/resetpostimage/5
        [HttpDelete("resetpostimage/{id:int}")]
        public async Task<IActionResult> ResetPostImage(int id)
        {
            var post = await _db.BlogPosts.Single(p => p.Id == id);
            post.Image = null;
            await _db.Complete();
            return Json("admin/editor/" + id);
        }

        async Task<Asset> SaveFile(IFormFile file)
        {
            var profile = await GetProfile();
            var storage = new BlogStorage(profile.Slug);
            var path = string.Format("{0}/{1}", DateTime.Now.Year, DateTime.Now.Month);

            var asset = await storage.UploadFormFile(file, Url.Content("~/"), path);

            // sometimes we just want to override uploaded file
            // only add DB record if asset does not exist yet
            var existingAsset = await _db.Assets.Where(a => a.Path == asset.Path).FirstOrDefaultAsync();
            if (existingAsset == null)
            {
                asset.ProfileId = profile.Id;
                asset.LastUpdated = SystemClock.Now();

                if (IsImageFile(asset.Url))
                {
                    asset.AssetType = AssetType.Image;
                }
                else
                {
                    asset.AssetType = AssetType.Attachment;
                }
                await _db.Assets.Add(asset);
            }
            else
            {
                existingAsset.LastUpdated = SystemClock.Now();
            }
            await _db.Complete();
            return asset;
        }

        async Task<Profile> GetProfile()
        {
            try
            {
                return await _db.Profiles.Single(p => p.IdentityName == User.Identity.Name);
            }
            catch
            {
                RedirectToAction("Login", "Account");
            }
            return null;
        }

        bool IsImageFile(string file)
        {
            if (file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }
    }
}
