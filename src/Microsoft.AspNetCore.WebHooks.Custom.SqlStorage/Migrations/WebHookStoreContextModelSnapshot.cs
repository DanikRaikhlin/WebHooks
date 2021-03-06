﻿// <auto-generated />
using Microsoft.AspNetCore.WebHooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.Internal;
using System;

namespace Microsoft.AspNetCore.WebHooks.Migrations
{
    [DbContext(typeof(WebHookStoreContext))]
    partial class WebHookStoreContextModelSnapshot : ModelSnapshot
    {
        protected override void BuildModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "2.0.1-rtm-125")
                .HasAnnotation("SqlServer:ValueGenerationStrategy", SqlServerValueGenerationStrategy.IdentityColumn);

            modelBuilder.Entity("Microsoft.AspNetCore.WebHooks.Storage.Registration", b =>
                {
                    b.Property<string>("Id")
                        .HasMaxLength(64);

                    b.Property<string>("User")
                        .HasMaxLength(256);

                    b.Property<string>("ProtectedData")
                        .IsRequired();

                    b.Property<byte[]>("RowVer")
                        .IsConcurrencyToken()
                        .ValueGeneratedOnAddOrUpdate();

                    b.HasKey("Id", "User");

                    b.ToTable("WebHooks");
                });
#pragma warning restore 612, 618
        }
    }
}
