using LuoliCommon.DTO.ConsumeInfo;
using LuoliCommon.Entities;
using ThirdApis;

namespace GatewayService.Services.ConsumeInfo
{
    public class ConsumeInfoRepository : IConsumeInfoRepository
    {
        private readonly AsynsApis _asynsApis;
        public ConsumeInfoRepository(AsynsApis asynsApis)
        {
            _asynsApis = asynsApis;
        }



        public async Task<ApiResponse<bool>> ConsumeInfoDelete(string goodsType, long id)
        {
          return  await _asynsApis.ConsumeInfoDelete(goodsType, id);
        }

        public async Task<ApiResponse<bool>> ConsumeInfoInsert(ConsumeInfoDTO dto)
        {
            return await _asynsApis.ConsumeInfoInsert(dto);
        }

        public async Task<ApiResponse<ConsumeInfoDTO>> ConsumeInfoQuery(string goodsType, long id)
        {
            return await _asynsApis.ConsumeInfoQuery(goodsType, id);
        }

        public async Task<ApiResponse<ConsumeInfoDTO>> ConsumeInfoQuery(string goodsType, string coupon)
        {
            return await _asynsApis.ConsumeInfoQuery(goodsType, coupon);
        }

        public async Task<ApiResponse<bool>> ConsumeInfoUpdate(UpdateRequest ur)
        {
           return await _asynsApis.ConsumeInfoUpdate(ur);
        }
    }
}
