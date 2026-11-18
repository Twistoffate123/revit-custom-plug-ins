using System;
using System.IO;
using System.Linq;
using FloorPatternFlattener.Services;
using FloorPatternFlattener.Storage;
using Xunit;

namespace FloorPatternFlattener.Tests
{
    public class StorageContractTests
    {
        [Fact]
        public void EveryFieldMapsToOneSetOverload()
        {
            Assert.NotEmpty(EsFieldCatalog.Fields);
            foreach (var field in EsFieldCatalog.Fields)
            {
                var choice = EsFieldCatalog.Choose(field.BuiltAs == "Number" || field.BuiltAs == "Length", false, field.BuiltAs);
                Assert.Equal(field.Choice, choice);
                if (field.BuiltAs == "Length")
                    Assert.Equal(EsUnitChoice.Feet, choice);
                else if (field.BuiltAs == "Number")
                    Assert.Equal(EsUnitChoice.General, choice);
                else
                    Assert.Equal(EsUnitChoice.NoUnit, choice);
            }
        }

        [Fact]
        public void LinkSchemaHasNoUnitOnAnyField()
        {
            var link = EsFieldCatalog.Fields.Where(f => f.SchemaName == "FPF_FlatPatternShape").ToList();
            Assert.Equal(new[] { "Version", "SourceFloorUniqueId", "ChunkIndex" }, link.Select(f => f.FieldName));
            Assert.All(link, f => Assert.Equal(EsUnitChoice.NoUnit, f.Choice));
        }

        [Fact]
        public void StoredLengthUsesFeetAndStoredNumberUsesGeneral()
        {
            var length = EsFieldCatalog.Choose(isDouble: true, specEmpty: false, specToken: "Length");
            var number = EsFieldCatalog.Choose(isDouble: true, specEmpty: false, specToken: "Number");
            Assert.Equal(EsUnitChoice.Feet, length);
            Assert.Equal(EsUnitChoice.General, number);
            Assert.Equal(1.0, EsFieldCatalog.ToStoredUnits(length, 304.8), 9);
            Assert.Equal(50.0, EsFieldCatalog.ToStoredUnits(number, 50), 9);
            Assert.Equal(50.0, EsFieldCatalog.FromStoredUnits(length, EsFieldCatalog.ToStoredUnits(length, 50)), 6);
            Assert.Equal(50.0, EsFieldCatalog.FromStoredUnits(number, 50), 9);

            Assert.Equal(EsUnitChoice.NoUnit, EsFieldCatalog.Choose(true, true, "Length"));
            Assert.Equal(EsUnitChoice.NoUnit, EsFieldCatalog.Choose(false, false, "Length"));
            Assert.Equal(EsUnitChoice.NoUnit, EsFieldCatalog.Choose(false, false, "Int"));
            Assert.Equal(EsUnitChoice.NoUnit, EsFieldCatalog.Choose(false, false, "String"));
            Assert.Equal(EsUnitChoice.FirstValid, EsFieldCatalog.Choose(true, false, "Area"));
        }

        [Fact]
        public void InstalledSharedParameterFileMatchesRevitHeader()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "FpfSharedParameters.txt");
            var text = File.ReadAllText(path);
            Assert.Null(SharedParameterFileCheck.Problem(text));
            Assert.Contains("*GROUP\tID\tNAME\r\n", text);
            Assert.Contains(SharedParameterFileCheck.ParamHeader + "\r\n", text);
            Assert.EndsWith("\t1\t0\r\n", text);
        }
    }
}
