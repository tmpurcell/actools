﻿using System.Globalization;
using FirstFloor.ModernUI.Helpers;
using FirstFloor.ModernUI.Windows.Converters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FirstFloor.ModernUI.Tests {
    [TestClass]
    public class LocalizableTest {
        [TestMethod]
        public void TitleCaseTest() {
            CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en");
            Assert.AreEqual("Abc Def Qwe", "abc def qwe".ToTitle());
            Assert.AreEqual("A as Because Although if A", "a as because although if a".ToTitle());
            Assert.AreEqual("The the The", "the the the".ToTitle());
        }

        [TestMethod]
        public void PluralizingTest() {
            CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en");
            Assert.AreEqual("hour", PluralizingConverter.PluralizeExt(1, "hour"));
            Assert.AreEqual("hours", PluralizingConverter.PluralizeExt(5, "hour"));
            Assert.AreEqual("1 hour", PluralizingConverter.PluralizeExt(1, "{0} hour"));
            Assert.AreEqual("2 hours", PluralizingConverter.PluralizeExt(2, "{0} hour"));
            Assert.AreEqual("5 hours", PluralizingConverter.PluralizeExt(5, "{0} hour"));
            Assert.AreEqual("11 hours", PluralizingConverter.PluralizeExt(11, "{0} hour"));
            Assert.AreEqual("21 hours", PluralizingConverter.PluralizeExt(21, "{0} hour"));

            CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("ru");
            Assert.AreEqual("1 час", PluralizingConverter.PluralizeExt(1, "{0} час"));
            Assert.AreEqual("2 часа", PluralizingConverter.PluralizeExt(2, "{0} час"));
            Assert.AreEqual("5 часов", PluralizingConverter.PluralizeExt(5, "{0} час"));
            Assert.AreEqual("11 часов", PluralizingConverter.PluralizeExt(11, "{0} час"));
            Assert.AreEqual("21 час", PluralizingConverter.PluralizeExt(21, "{0} час"));

            CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("es");
            Assert.AreEqual("1 libro", PluralizingConverter.PluralizeExt(1, "{0} libro"));
            Assert.AreEqual("5 libros", PluralizingConverter.PluralizeExt(5, "{0} libro"));
            Assert.AreEqual("5 borradores", PluralizingConverter.PluralizeExt(5, "{0} borrador"));
            Assert.AreEqual("4 los aviones", PluralizingConverter.PluralizeExt(4, "{0} {el avión}"));

            /* no-break version */
            Assert.AreEqual("5 los aviones", PluralizingConverter.PluralizeExt(5, "{0} el avión"));
            Assert.AreEqual("5 los lápices", PluralizingConverter.PluralizeExt(5, "{0} el lápiz"));
            Assert.AreEqual("5 los aviones", PluralizingConverter.PluralizeExt(5, "{0} el avión"));
        }
    }
}